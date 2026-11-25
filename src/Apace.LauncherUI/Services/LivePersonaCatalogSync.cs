using Serilog;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ILogger = Serilog.ILogger;

namespace Apace.LauncherUI.Services;

// Live persona catalog sync (panel side).
//
// Walks the still-live PlayFab marketplace of the Minecraft Earth title (20ca2 —
// the same anonymous AndroidDeviceID login scripts/fetch_persona_catalog.py uses)
// and caches the whole PersonaDurable catalog in the persistent data volume:
//
//   <mojang-assets>/persona-catalog/index.json       deduped per-piece summary (facts)
//   <mojang-assets>/persona-catalog/items/<id>.json  raw PlayFab item metadata (facts)
//
// The volume is the only place these data live — nothing catalog-shaped is
// committed to the repo. The ApiServer reads the same volume (Services/
// LivePersonaCatalog) and builds the client offer from it with the unlock-all
// strip; binaries and thumbnails are downloaded lazily, on the first client
// request, never during the sync.
//
// Sync triggers: every panel start when the volume index is missing or older
// than PersonaCatalogSyncIntervalHours (default 24), plus the admin panel's
// "Sync now" button. Polite by design: one request per ~0.7 s, skip/top
// pagination, full catalog ≈ 400 pages (Server count ~40k entries, ~12k unique
// pieces after dedupe).
//
// Pagination note: the live title-20ca2 Catalog/Search does not honour
// ContinuationToken (verified 22.11: the token comes back absent or the same
// page repeats; scripts/fetch_persona_catalog.py always used skip/top) — so the
// walk pages with skip, deduped per piece below.
public static class LivePersonaCatalogSync
{
    private const string CatalogDirName = "persona-catalog";
    private const string IndexFileName = "index.json";
    private const string ItemsDirName = "items";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions RawItemJsonOptions = new(JsonSerializerDefaults.General) { WriteIndented = false };

    public static string CatalogDir => Path.Combine(MojangAssetPaths.MojangAssetsDir, CatalogDirName);
    public static string IndexPath => Path.Combine(CatalogDir, IndexFileName);
    public static string ItemsDir => Path.Combine(CatalogDir, ItemsDirName);

    // Index shapes — must stay in sync with the ApiServer's LivePersonaCatalog
    // records (separate processes share the volume, not the code).
    public sealed record SyncItemReference(Guid Id, int Amount);

    public sealed record SyncIndexItem(
        string Id,
        string Title,
        string Description,
        Dictionary<string, List<string>>? Keywords,
        List<string> Tags,
        string PieceType,
        Guid PieceId,
        string PieceVersion,
        string Rarity,
        string? CreatorName,
        string? CreatorEntityId,
        Guid? OfferId,
        List<SyncItemReference> ItemReferences,
        string? ThumbnailId,
        string? ThumbnailUrl,
        DateTime CreationDate,
        DateTime LastModifiedDate,
        DateTime? StartDate,
        bool IsEmote);

    public sealed record SyncIndex(DateTimeOffset? SyncedAt, int RawItemCount, int EmoteCount, List<SyncIndexItem> Items, List<SyncSkinPack> SkinPacks);

    // One thematic skin pack (MarketplaceDurableCatalog_V1.2 bundle) — the offers
    // behind the dressing room's "Classic Skins" row, which the client builds
    // from the layout manifest's skinPackFeaturedQuery.productIds.
    public sealed record SyncSkinPack(
        string Id,
        string Title,
        string PackUuid,
        string PackVersion,
        string? CreatorName,
        int SkinCount,
        string? ThumbnailId,
        string? ThumbnailUrl,
        List<string> Tags,
        List<SyncItemReference> ItemReferences,
        DateTime CreationDate,
        DateTime LastModifiedDate);

    // ---------------------------------------------------------------------------
    // Status (panel UI)
    // ---------------------------------------------------------------------------

    public static bool IsSyncing { get; private set; }
    public static DateTimeOffset? LastSyncFinished { get; private set; }
    public static string? LastError { get; private set; }
    public static int PagesFetched { get; private set; }
    public static int RawItemsFetched { get; private set; }

    /// <summary>Last sync recorded in the volume (null = never synced).</summary>
    public static DateTimeOffset? GetLastVolumeSync()
    {
        try
        {
            if (!File.Exists(IndexPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(IndexPath));
            return doc.RootElement.TryGetProperty("syncedAt", out var syncedAt) && syncedAt.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(syncedAt.GetString(), out var parsed)
                    ? parsed
                    : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Offer counters for the panel: unique/emote pieces + probe liveness from the volume.</summary>
    public static (int Unique, int Emotes, int Live, int Dead, int CachedBinaries, int CachedThumbnails) GetCounters()
    {
        int unique = 0, emotes = 0;
        try
        {
            if (File.Exists(IndexPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(IndexPath));
                unique = doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                    ? items.GetArrayLength()
                    : 0;
                emotes = doc.RootElement.TryGetProperty("emoteCount", out var emoteCount) ? emoteCount.GetInt32() : 0;
            }
        }
        catch
        {
            // unreadable index — report zeros
        }

        // the ApiServer writes probe-state.json into the same volume as the client
        // browses; the panel only reads it here
        int live = 0, dead = 0, binaries = 0, thumbnails = 0;
        try
        {
            string probePath = Path.Combine(CatalogDir, "probe-state.json");
            if (File.Exists(probePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(probePath));
                if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
                {
                    foreach (var entry in items.EnumerateObject())
                    {
                        string? state = entry.Value.TryGetProperty("state", out var stateProp) ? stateProp.GetString() : null;
                        if (state == "live")
                        {
                            live++;
                        }
                        else if (state == "dead")
                        {
                            dead++;
                        }
                    }
                }
            }

            string binariesDir = Path.Combine(CatalogDir, "binaries");
            if (Directory.Exists(binariesDir))
            {
                binaries = Directory.EnumerateFiles(binariesDir, "*.zip").Count();
            }

            string thumbnailsDir = Path.Combine(CatalogDir, "thumbnails");
            if (Directory.Exists(thumbnailsDir))
            {
                thumbnails = Directory.EnumerateFiles(thumbnailsDir).Count();
            }
        }
        catch
        {
            // counters are informational
        }

        return (unique, emotes, live, dead, binaries, thumbnails);
    }

    // ---------------------------------------------------------------------------
    // Scheduling
    // ---------------------------------------------------------------------------

    private static int _scheduledRunStarted;

    /// <summary>
    /// Starts the background sync when it is due: the toggle is on and the
    /// volume index is missing or older than the configured interval. Safe to
    /// call from every startup path.
    /// </summary>
    public static void EnsureScheduledSyncStarted(Settings settings, ILogger logger)
    {
        if (settings.LivePersonaCatalog is false)
        {
            return; // toggle off — the live marketplace is disabled
        }

        if (IsSyncing)
        {
            return;
        }

        DateTimeOffset? lastSync = GetLastVolumeSync();
        double intervalHours = Math.Clamp(settings.PersonaCatalogSyncIntervalHours ?? 24, 1, 168);
        if (lastSync is not null && DateTimeOffset.UtcNow - lastSync < TimeSpan.FromHours(intervalHours))
        {
            logger.Debug("Live persona catalog synced {Ago} ago — next sync in {Remaining}", DateTimeOffset.UtcNow - lastSync,
                TimeSpan.FromHours(intervalHours) - (DateTimeOffset.UtcNow - lastSync));
            return;
        }

        if (Interlocked.Exchange(ref _scheduledRunStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                logger.Information("Live persona catalog sync starting (background, last sync: {Last})", lastSync?.ToString("o", CultureInfo.InvariantCulture) ?? "never");
                await SyncNowAsync(logger, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Live persona catalog sync failed");
            }
            finally
            {
                Interlocked.Exchange(ref _scheduledRunStarted, 0);
            }
        });
    }

    // ---------------------------------------------------------------------------
    // The sync
    // ---------------------------------------------------------------------------

    /// <summary>Full catalog sync: anonymous login → skip/top pagination → volume cache. Never throws.</summary>
    public static async Task<bool> SyncNowAsync(ILogger logger, CancellationToken cancellationToken)
    {
        if (IsSyncing)
        {
            logger.Warning("A live persona catalog sync is already in progress");
            return false;
        }

        IsSyncing = true;
        LastError = null;
        PagesFetched = 0;
        RawItemsFetched = 0;
        try
        {
            Directory.CreateDirectory(CatalogDir);
            Directory.CreateDirectory(ItemsDir);

            // 1. anonymous AndroidDeviceID login → master_player_account entity token
            string masterToken = await LoginAsync(cancellationToken);
            logger.Information("Live persona catalog sync: logged in to PlayFab 20ca2 anonymously");

            // 2. walk the whole PersonaDurable catalog (~40k entries). PlayFab caps
            // skip at 10000 ("Skip must be between 0 and 10000, inclusively") — a
            // plain paged walk would silently stop at ~10k entries — so the walk
            // pages per creationDate window instead: any window holding more than
            // ~9.5k entries is bisected on creationDate until each fits under the
            // skip cap, then pages linearly with OrderBy creationDate asc.
            var byPiece = new Dictionary<Guid, SyncIndexItem>();
            int emotes = 0, thumbUrls = 0;
            var pieceTypes = new SortedDictionary<string, int>();
            const int PageSize = 100;
            const int SkipCap = 10000;
            const int WindowSplitThreshold = 9500;

            async Task<int> ProcessPage(JsonElement data)
            {
                int onPage = 0;
                if (data.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var raw in items.EnumerateArray())
                    {
                        onPage++;
                        // raw item metadata lands in the volume verbatim (facts only)
                        await WriteRawItemAsync(raw, cancellationToken);

                        var record = BuildIndexRecord(raw);
                        if (record is null)
                        {
                            continue; // not a persona_piece PersonaDurable (or no piece id)
                        }

                        if (record.IsEmote)
                        {
                            emotes++;
                        }
                        else
                        {
                            pieceTypes[record.PieceType] = pieceTypes.TryGetValue(record.PieceType, out int count) ? count + 1 : 1;
                        }

                        if (!string.IsNullOrWhiteSpace(record.ThumbnailUrl) && record.ThumbnailUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            thumbUrls++;
                        }

                        // dedupe: the marketplace keeps every re-publication of a piece;
                        // the offer wants one entry per piece uuid — the newest wins
                        if (byPiece.TryGetValue(record.PieceId, out var existing) && existing.CreationDate >= record.CreationDate)
                        {
                            continue;
                        }

                        byPiece[record.PieceId] = record;
                    }
                }

                return onPage;
            }

            async Task<int> PageAsync(JsonElement data)
            {
                int onPage = await ProcessPage(data);
                PagesFetched++;
                RawItemsFetched += onPage;

                if (PagesFetched % 25 == 0)
                {
                    logger.Information("Live persona catalog sync: {Pages} page(s), {Raw} raw item(s), {Unique} unique piece(s) so far",
                        PagesFetched, RawItemsFetched, byPiece.Count);

                    // checkpoint: land the partial index during the walk so a redeploy
                    // mid-sync still serves the pieces seen so far instead of nothing.
                    // SyncedAt stays null on checkpoints — the scheduler keeps treating
                    // the walk as due until a completed run stamps the final index.
                    if (byPiece.Count > 0)
                    {
                        SyncIndex checkpoint = new(null, RawItemsFetched, emotes, [.. byPiece.Values.OrderBy(item => item.PieceType).ThenBy(item => item.Title)], []);
                        try
                        {
                            await WriteIndexAsync(checkpoint, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            // a checkpoint failure must never kill the walk — the final
                            // write below is what matters; log it and keep paging
                            logger.Warning("Live persona catalog sync: checkpoint write failed ({Error}) — continuing", ex.Message);
                        }
                    }
                }

                return onPage;
            }

            string baseFilter = "(contentType eq 'PersonaDurable')";
            DateTime? catalogMin = FirstCreationDate(await PostCatalogSearchAsync(masterToken, baseFilter, "creationDate asc", 0, 1, cancellationToken));
            DateTime? catalogMax = FirstCreationDate(await PostCatalogSearchAsync(masterToken, baseFilter, "creationDate desc", 0, 1, cancellationToken));
            if (catalogMin is not null && catalogMax is not null)
            {
                var windows = new Stack<(DateTime Lo, DateTime Hi)>();
                windows.Push((catalogMin.Value, catalogMax.Value.AddSeconds(1)));
                while (windows.Count > 0 && !cancellationToken.IsCancellationRequested)
                {
                    (DateTime lo, DateTime hi) = windows.Pop();
                    string filter = WindowFilter(baseFilter, lo, hi);
                    JsonElement probe = await PostCatalogSearchAsync(masterToken, filter, "creationDate asc", 0, 1, cancellationToken);
                    int windowCount = probe.TryGetProperty("Count", out var windowCountProp) && windowCountProp.ValueKind == JsonValueKind.Number
                        ? windowCountProp.GetInt32()
                        : 0;
                    if (windowCount == 0)
                    {
                        continue;
                    }

                    if (windowCount > WindowSplitThreshold && hi - lo > TimeSpan.FromMilliseconds(1))
                    {
                        DateTime mid = lo + TimeSpan.FromTicks((hi - lo).Ticks / 2);
                        windows.Push((mid, hi));
                        windows.Push((lo, mid));
                        continue;
                    }

                    if (windowCount > SkipCap)
                    {
                        logger.Warning(
                            "Live persona catalog sync: window {Lo} .. {Hi} holds {Count} entries within one millisecond — paging stops at the PlayFab skip cap and the excess is skipped",
                            lo.ToString("o", CultureInfo.InvariantCulture), hi.ToString("o", CultureInfo.InvariantCulture), windowCount);
                    }

                    for (int skip = 0; skip < SkipCap; skip += PageSize)
                    {
                        JsonElement data = await PostCatalogSearchAsync(masterToken, filter, "creationDate asc", skip, PageSize, cancellationToken);
                        int onPage = await PageAsync(data);
                        bool pageShort = onPage < PageSize;
                        if (onPage == 0 || pageShort)
                        {
                            break;
                        }

                        // polite: ~1.4 req/s against the marketplace for the whole walk
                        await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
                    }
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                LastError = "sync cancelled";
                return false;
            }

            // 3. walk the thematic skin packs — MarketplaceDurableCatalog_V1.2 bundles
            // ("Classic Skins" row of the dressing room). A small catalog (~1.4k
            // entries), so a plain paged walk stays far below the PlayFab skip cap.
            var skinPacksByUuid = new Dictionary<string, SyncSkinPack>();
            const string packFilter = "(contentType eq 'MarketplaceDurableCatalog_V1.2')";
            for (int skip = 0; skip < SkipCap; skip += PageSize)
            {
                JsonElement data = await PostCatalogSearchAsync(masterToken, packFilter, "creationDate asc", skip, PageSize, cancellationToken);
                int onPage = 0;
                if (data.TryGetProperty("Items", out var packs) && packs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var raw in packs.EnumerateArray())
                    {
                        onPage++;
                        if (BuildSkinPackRecord(raw) is not { } packRecord)
                        {
                            continue;
                        }

                        // newest republication wins, same rule as pieces
                        if (skinPacksByUuid.TryGetValue(packRecord.PackUuid, out var existingPack) && existingPack.CreationDate >= packRecord.CreationDate)
                        {
                            continue;
                        }

                        skinPacksByUuid[packRecord.PackUuid] = packRecord;
                    }
                }

                PagesFetched++;
                if (onPage < PageSize)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
            }

            logger.Information("Live persona catalog sync: {Count} thematic skin pack(s) indexed", skinPacksByUuid.Count);

            // 4. write the deduped index (atomic replace; the ApiServer hot-reloads it)
            var index = new SyncIndex(DateTimeOffset.UtcNow, RawItemsFetched, emotes, [.. byPiece.Values.OrderBy(item => item.PieceType).ThenBy(item => item.Title)], [.. skinPacksByUuid.Values.OrderBy(item => item.Title)]);
            await WriteIndexAsync(index, cancellationToken);

            LastSyncFinished = DateTimeOffset.UtcNow;
            logger.Information(
                "Live persona catalog sync complete: {Raw} raw item(s) over {Pages} page(s) — {Unique} unique persona piece(s) indexed ({Types}), emotes: {Emotes} (not offered — see docs), thumbnails with a live URL: {Thumbs}. " +
                "Search never returns Contents — piece binary URLs are resolved lazily per item via Catalog/GetItem on the first client request",
                RawItemsFetched, PagesFetched, index.Items.Count, string.Join(", ", pieceTypes.Select(kv => $"{kv.Key}={kv.Value}")), emotes, thumbUrls);
            return true;
        }
        catch (OperationCanceledException)
        {
            LastError = "sync cancelled";
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            // message in the template line: launcher consoles usually print only
            // the message, not the exception — keep the cause visible there too
            logger.Error(ex, "Live persona catalog sync failed: {Error}", ex.Message);
            return false;
        }
        finally
        {
            IsSyncing = false;
        }
    }

    // Atomic index write: temp file first, then an overwrite move — a crash can
    // never leave the ApiServer (or the panel counters) reading a half-written index.
    private static async Task WriteIndexAsync(SyncIndex index, CancellationToken cancellationToken)
    {
        string tempPath = IndexPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(index, JsonOptions), cancellationToken);
        // the ApiServer and the panel counters read this file on the same volume;
        // on Windows a concurrent reader briefly blocks the overwrite move — retry
        // instead of failing a multi-minute walk at the very last step
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, IndexPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
    }

    private static async Task<string> LoginAsync(CancellationToken cancellationToken)
    {
        var login = await PostAsync("/Client/LoginWithAndroidDeviceID", new Dictionary<string, object>
        {
            ["TitleId"] = "20ca2",
            ["AndroidDeviceId"] = "apace-catalog-fetcher",
            ["AndroidDevice"] = "Pixel",
            ["OS"] = "Linux",
            ["CreateAccount"] = true,
        }, [], cancellationToken);
        string clientToken = login.GetProperty("data").GetProperty("EntityToken").GetProperty("EntityToken").GetString()
            ?? throw new InvalidOperationException("login returned no entity token");
        string playFabId = login.GetProperty("data").GetProperty("PlayFabId").GetString() ?? "";

        var exchange = await PostAsync("/Authentication/GetEntityToken", new Dictionary<string, object>
        {
            ["Entity"] = new Dictionary<string, object> { ["Id"] = playFabId, ["Type"] = "master_player_account" },
        }, [clientToken], cancellationToken);
        string master = exchange.GetProperty("data").GetProperty("EntityToken").GetString()
            ?? throw new InvalidOperationException("entity token exchange returned no token");
        return master;
    }

    private static async Task<JsonElement> PostAsync(string path, Dictionary<string, object> body, IReadOnlyList<string> entityTokens, CancellationToken cancellationToken)
    {
        // the panel shares the server IP with every proxied client call; PlayFab
        // answers bursts with 429 (and occasionally 5xx) — retry transient
        // failures with backoff instead of killing a multi-minute walk, and put
        // the status + body into the exception so LastError / the console line
        // shows the real cause instead of a bare EnsureSuccessStatusCode text
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var client = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(40),
                };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ApaceProject/Apace live-persona-catalog-sync");
                using var request = new HttpRequestMessage(HttpMethod.Post, $"https://20ca2.playfabapi.com{path}")
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
                };
                if (entityTokens.Count > 0)
                {
                    request.Headers.Add("X-EntityToken", entityTokens[^1]);
                }

                using var response = await client.SendAsync(request, cancellationToken);
                string payload = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    string snippet = payload.Trim();
                    if (snippet.Length > 300)
                    {
                        snippet = snippet[..300];
                    }

                    throw new HttpRequestException($"PlayFab {path} returned HTTP {(int)response.StatusCode} ({response.StatusCode}): {snippet}");
                }

                using var doc = JsonDocument.Parse(payload);
                return doc.RootElement.Clone();
            }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 3), cancellationToken);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException hre when hre.StatusCode is null
            || hre.StatusCode == System.Net.HttpStatusCode.TooManyRequests
            || (int)hre.StatusCode >= 500 => true,
        // HttpClient timeouts surface as TaskCanceledException with a clean token
        TaskCanceledException tce when !tce.CancellationToken.IsCancellationRequested => true,
        IOException => true,
        _ => false,
    };

    private static async Task<JsonElement> PostCatalogSearchAsync(string masterToken, string filter, string orderBy, int skip, int top, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["Filter"] = filter,
            ["OrderBy"] = orderBy,
            ["scid"] = "4fc10100-5f7a-4470-899b-280835760c07",
            ["skip"] = skip,
            ["top"] = top,
        };

        var result = await PostAsync("/Catalog/Search", body, [masterToken], cancellationToken);
        return result.GetProperty("data");
    }

    /// <summary>Half-open [lo, hi) creationDate window over the persona content type.</summary>
    private static string WindowFilter(string baseFilter, DateTime lo, DateTime hi)
    {
        return $"{baseFilter} and creationDate ge {FormatDate(lo)} and creationDate lt {FormatDate(hi)}";
    }

    private static string FormatDate(DateTime date)
    {
        return date.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>CreationDate of the first item of a search result (null when the page is empty).</summary>
    private static DateTime? FirstCreationDate(JsonElement data)
    {
        if (data.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0
            && items[0].TryGetProperty("CreationDate", out var created) && created.ValueKind == JsonValueKind.String
            && DateTime.TryParse(created.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private static async Task WriteRawItemAsync(JsonElement raw, CancellationToken cancellationToken)
    {
        string? id = raw.TryGetProperty("Id", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || id.Contains('/') || id.Contains('\\') || id.Contains(".."))
        {
            return;
        }

        string path = Path.Combine(ItemsDir, $"{id}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(raw, RawItemJsonOptions), cancellationToken);
    }

    /// <summary>
    /// Turns a raw PlayFab PersonaDurable entry into the index record (facts from
    /// the marketplace — the unlock-all strip happens on the offer output in the
    /// ApiServer, never here). Null when the entry is not a persona_piece.
    /// </summary>
    private static SyncIndexItem? BuildIndexRecord(JsonElement raw)
    {
        string id = raw.GetProperty("Id").GetString() ?? "";
        var display = raw.TryGetProperty("DisplayProperties", out var dp) && dp.ValueKind == JsonValueKind.Object ? dp : default;

        string? pieceType = display.ValueKind != JsonValueKind.Undefined && display.TryGetProperty("pieceType", out var ptProp) && ptProp.ValueKind == JsonValueKind.String
            ? ptProp.GetString()
            : null;
        JsonElement firstPack = display.ValueKind != JsonValueKind.Undefined
            && display.TryGetProperty("packIdentity", out var packs) && packs.ValueKind == JsonValueKind.Array && packs.GetArrayLength() > 0
                ? packs[0]
                : default;
        string? packType = firstPack.ValueKind != JsonValueKind.Undefined && firstPack.TryGetProperty("type", out var packTypeProp) ? packTypeProp.GetString() : null;
        string? pieceIdRaw = firstPack.ValueKind != JsonValueKind.Undefined && firstPack.TryGetProperty("uuid", out var uuidProp) && uuidProp.ValueKind == JsonValueKind.String
            ? uuidProp.GetString()
            : null;
        string pieceVersion = firstPack.ValueKind != JsonValueKind.Undefined && firstPack.TryGetProperty("version", out var versionProp) && versionProp.ValueKind == JsonValueKind.String
            ? versionProp.GetString() ?? "1.0.0"
            : "1.0.0";

        if (pieceType is null || pieceIdRaw is null || packType != "persona_piece" || !Guid.TryParse(pieceIdRaw, out Guid pieceId))
        {
            return null;
        }

        var tags = new List<string>();
        if (raw.TryGetProperty("Tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tagsProp.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.String && tag.GetString() is { } tagValue)
                {
                    tags.Add(tagValue);
                }
            }
        }

        // hidden_offer entries never appeared in the dressing room (StoreConfig
        // globalNotTags) — recorded in items/ but not offered
        if (tags.Contains("hidden_offer", StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var keywords = new Dictionary<string, List<string>>();
        if (raw.TryGetProperty("Keywords", out var keywordsProp) && keywordsProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var keyword in keywordsProp.EnumerateObject())
            {
                if (keyword.Value.ValueKind == JsonValueKind.Object && keyword.Value.TryGetProperty("Values", out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    var list = values.EnumerateArray()
                        .Where(v => v.ValueKind == JsonValueKind.String)
                        .Select(v => v.GetString() ?? "")
                        .Where(v => v.Length > 0)
                        .ToList();
                    if (list.Count > 0)
                    {
                        keywords[keyword.Name] = list;
                    }
                }
            }
        }

        string? thumbnailId = null, thumbnailUrl = null;
        if (raw.TryGetProperty("Images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var image in images.EnumerateArray())
            {
                string? imageType = image.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() : null;
                if (imageType == "Thumbnail" || thumbnailId is null)
                {
                    thumbnailId = image.TryGetProperty("Id", out var imageIdProp) ? imageIdProp.GetString() : thumbnailId;
                    thumbnailUrl = image.TryGetProperty("Url", out var urlProp) ? urlProp.GetString() : thumbnailUrl;
                    if (imageType == "Thumbnail")
                    {
                        break;
                    }
                }
            }
        }

        var references = new List<SyncItemReference>();
        if (raw.TryGetProperty("ItemReferences", out var refsProp) && refsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var reference in refsProp.EnumerateArray())
            {
                if (reference.TryGetProperty("Id", out var refId) && refId.ValueKind == JsonValueKind.String && Guid.TryParse(refId.GetString(), out Guid refGuid))
                {
                    references.Add(new SyncItemReference(refGuid, reference.TryGetProperty("Amount", out var amountProp) && amountProp.ValueKind == JsonValueKind.Number ? amountProp.GetInt32() : 1));
                }
            }
        }

        DateTime creationDate = raw.TryGetProperty("CreationDate", out var createdProp) && DateTime.TryParse(createdProp.GetString(), out var created) ? created.ToUniversalTime() : DateTime.UtcNow;
        DateTime lastModified = raw.TryGetProperty("LastModifiedDate", out var modifiedProp) && DateTime.TryParse(modifiedProp.GetString(), out var modified) ? modified.ToUniversalTime() : creationDate;
        DateTime? startDate = raw.TryGetProperty("StartDate", out var startProp) && startProp.ValueKind == JsonValueKind.String && DateTime.TryParse(startProp.GetString(), out var start) ? start.ToUniversalTime() : null;

        return new SyncIndexItem(
            id,
            raw.TryGetProperty("Title", out var titleProp) ? FirstTranslation(titleProp) : id,
            raw.TryGetProperty("Description", out var descProp) ? FirstTranslation(descProp) : "§",
            keywords.Count > 0 ? keywords : null,
            tags,
            pieceType,
            pieceId,
            pieceVersion,
            display.ValueKind != JsonValueKind.Undefined && display.TryGetProperty("rarity", out var rarityProp) && rarityProp.ValueKind == JsonValueKind.String ? rarityProp.GetString() ?? "common" : "common",
            display.ValueKind != JsonValueKind.Undefined && display.TryGetProperty("creatorName", out var creatorProp) && creatorProp.ValueKind == JsonValueKind.String ? creatorProp.GetString() : "Minecraft",
            raw.TryGetProperty("CreatorEntityKey", out var creatorKeyProp) && creatorKeyProp.TryGetProperty("Id", out var creatorIdProp) ? creatorIdProp.GetString() : "301F442C3B63DC20",
            display.ValueKind != JsonValueKind.Undefined && display.TryGetProperty("offerId", out var offerProp) && offerProp.ValueKind == JsonValueKind.String && Guid.TryParse(offerProp.GetString(), out Guid offerGuid) ? offerGuid : null,
            references,
            thumbnailId,
            thumbnailUrl,
            creationDate,
            lastModified,
            startDate,
            pieceType == "persona_emote"
        );
    }

    private static string FirstTranslation(JsonElement translations)
    {
        foreach (string key in new[] { "neutral", "NEUTRAL", "en-US" })
        {
            if (translations.ValueKind == JsonValueKind.Object && translations.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!;
            }
        }

        return "§";
    }

    /// <summary>
    /// Turns a raw MarketplaceDurableCatalog_V1.2 bundle into the skin pack index
    /// record. Null for anything that is not a dressing-room skin pack: only
    /// packIdentity type "skinpack" entries feed the Classic Skins row
    /// (worldtemplate/resourcepack/behaviorpack bundles are store-only), and
    /// hidden_offer entries never appeared in the store (same rule as pieces).
    /// </summary>
    private static SyncSkinPack? BuildSkinPackRecord(JsonElement raw)
    {
        string id = raw.GetProperty("Id").GetString() ?? "";
        var display = raw.TryGetProperty("DisplayProperties", out var dp) && dp.ValueKind == JsonValueKind.Object ? dp : default;

        JsonElement firstPack = display.ValueKind != JsonValueKind.Undefined
            && display.TryGetProperty("packIdentity", out var packs) && packs.ValueKind == JsonValueKind.Array && packs.GetArrayLength() > 0
                ? packs[0]
                : default;
        string? packType = firstPack.ValueKind != JsonValueKind.Undefined && firstPack.TryGetProperty("type", out var packTypeProp) ? packTypeProp.GetString() : null;
        string? packUuid = firstPack.ValueKind != JsonValueKind.Undefined && firstPack.TryGetProperty("uuid", out var packUuidProp) ? packUuidProp.GetString() : null;
        string packVersion = firstPack.ValueKind != JsonValueKind.Undefined && firstPack.TryGetProperty("version", out var packVersionProp) && packVersionProp.ValueKind == JsonValueKind.String
            ? packVersionProp.GetString() ?? "1.0.0"
            : "1.0.0";

        if (packType != "skinpack" || packUuid is null || !Guid.TryParse(packUuid, out _))
        {
            return null;
        }

        var tags = new List<string>();
        if (raw.TryGetProperty("Tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tagsProp.EnumerateArray())
            {
                if (tag.ValueKind == JsonValueKind.String && tag.GetString() is { } tagValue)
                {
                    tags.Add(tagValue);
                }
            }
        }

        if (tags.Contains("hidden_offer", StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        int skinCount = display.ValueKind != JsonValueKind.Undefined
            && display.TryGetProperty("skinPack", out var skinPackProp) && skinPackProp.TryGetProperty("count", out var countProp) && countProp.ValueKind == JsonValueKind.Number
                ? countProp.GetInt32()
                : 0;

        string? thumbnailId = null, thumbnailUrl = null;
        if (raw.TryGetProperty("Images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var image in images.EnumerateArray())
            {
                string? imageType = image.TryGetProperty("Type", out var typeProp) ? typeProp.GetString() : null;
                if (imageType == "Thumbnail" || thumbnailId is null)
                {
                    thumbnailId = image.TryGetProperty("Id", out var imageIdProp) ? imageIdProp.GetString() : thumbnailId;
                    thumbnailUrl = image.TryGetProperty("Url", out var urlProp) ? urlProp.GetString() : thumbnailUrl;
                    if (imageType == "Thumbnail")
                    {
                        break;
                    }
                }
            }
        }

        var references = new List<SyncItemReference>();
        if (raw.TryGetProperty("ItemReferences", out var refsProp) && refsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var reference in refsProp.EnumerateArray())
            {
                if (reference.TryGetProperty("Id", out var refId) && refId.ValueKind == JsonValueKind.String && Guid.TryParse(refId.GetString(), out Guid refGuid))
                {
                    references.Add(new SyncItemReference(refGuid, reference.TryGetProperty("Amount", out var amountProp) && amountProp.ValueKind == JsonValueKind.Number ? amountProp.GetInt32() : 1));
                }
            }
        }

        DateTime creationDate = raw.TryGetProperty("CreationDate", out var createdProp) && DateTime.TryParse(createdProp.GetString(), out var created) ? created.ToUniversalTime() : DateTime.UtcNow;
        DateTime lastModified = raw.TryGetProperty("LastModifiedDate", out var modifiedProp) && DateTime.TryParse(modifiedProp.GetString(), out var modified) ? modified.ToUniversalTime() : creationDate;

        return new SyncSkinPack(
            id,
            raw.TryGetProperty("Title", out var titleProp) ? FirstTranslation(titleProp) : id,
            packUuid,
            packVersion,
            display.ValueKind != JsonValueKind.Undefined && display.TryGetProperty("creatorName", out var creatorProp) && creatorProp.ValueKind == JsonValueKind.String ? creatorProp.GetString() : "Minecraft",
            skinCount,
            thumbnailId,
            thumbnailUrl,
            tags,
            references,
            creationDate,
            lastModified
        );
    }

    // ---------------------------------------------------------------------------
    // Lazy thumbnail ensure (PersonaImagesController miss path — the panel-side
    // mirror of the ApiServer's LivePersonaCatalog.TryEnsureThumbnailAsync)
    // ---------------------------------------------------------------------------

    // Runtime thumbnail mirror: raw.githubusercontent.com serves the mirrored
    // Bedrock persona images, flat assets/images/persona_piece/<thumbnailId>.png
    // (research/bedrock-persona-report.md: keyed by thumbnail id, same scope as
    // the 20ca2 Catalog/Search — verified live 23.11). URL fact only, nothing
    // committed (same fallback the ApiServer uses).
    private const string ThumbnailMirrorBase = "https://raw.githubusercontent.com/Daniel-Ric/Bedrock-Persona/main/assets/images/persona_piece";

    private static readonly HttpClient ThumbnailHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly SemaphoreSlim ThumbnailGate = new(1, 1);
    private static DateTimeOffset _nextThumbnailFetch = DateTimeOffset.MinValue;

    private sealed record ThumbIndexItem(string? ThumbnailId, string? ThumbnailUrl);
    private sealed record ThumbIndex(List<ThumbIndexItem> Items);

    // thumbnailId → recorded URL, cached per index file stamp — the synced index
    // is the only source of live thumbnail URLs on the panel side.
    private static (long Stamp, Dictionary<string, string?> Map)? _thumbnailMapCache;

    private static Dictionary<string, string?>? ThumbnailMap()
    {
        try
        {
            if (!File.Exists(IndexPath))
            {
                return null;
            }

            long stamp = File.GetLastWriteTimeUtc(IndexPath).Ticks;
            if (_thumbnailMapCache?.Stamp == stamp)
            {
                return _thumbnailMapCache.Value.Map;
            }

            var index = JsonSerializer.Deserialize<ThumbIndex>(File.ReadAllText(IndexPath), JsonOptions);
            var map = (index?.Items ?? [])
                .Where(item => !string.IsNullOrEmpty(item.ThumbnailId))
                .GroupBy(item => item.ThumbnailId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().ThumbnailUrl, StringComparer.OrdinalIgnoreCase);
            _thumbnailMapCache = (stamp, map);
            return map;
        }
        catch
        {
            return _thumbnailMapCache?.Map;
        }
    }

    // One thumbnail fetch per ~0.7 s — the same politeness as the sync pages.
    private static async Task PaceThumbnailAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset waitUntil = _nextThumbnailFetch;
        if (waitUntil > DateTimeOffset.UtcNow)
        {
            await Task.Delay(waitUntil - DateTimeOffset.UtcNow, cancellationToken);
        }

        _nextThumbnailFetch = DateTimeOffset.UtcNow.AddMilliseconds(700);
    }

    /// <summary>
    /// The skins tab asked for a thumbnail that is not in any local dir: download
    /// it from the thumbnail URL recorded at sync time, else from the
    /// Bedrock-Persona mirror. Returns the cached file path or null. Mirror of
    /// the ApiServer's resolver, so the panel serves thumbnails without waiting
    /// for a game client to warm the cache first.
    /// </summary>
    public static async Task<string?> TryEnsureThumbnailAsync(string imageId, ILogger logger, CancellationToken cancellationToken)
    {
        var map = ThumbnailMap();
        string? recordedUrl = map is not null && map.TryGetValue(imageId, out var url) ? url : null;
        if (recordedUrl is null && map is null)
        {
            return null; // no synced index — the mirror would only cover search-scope ids blind
        }

        string path = Path.Combine(CatalogDir, "thumbnails", imageId + ".png");
        await ThumbnailGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return path;
            }

            string mirrorUrl = $"{ThumbnailMirrorBase}/{imageId}.png";
            foreach (string candidate in new[] { recordedUrl, mirrorUrl }.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct())
            {
                try
                {
                    await PaceThumbnailAsync(cancellationToken);
                    using var response = await ThumbnailHttpClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        continue;
                    }

                    string partPath = path + ".part";
                    await using (var content = await response.Content.ReadAsStreamAsync(cancellationToken))
                    await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await content.CopyToAsync(fileStream, cancellationToken);
                    }

                    // the mirror has a handful of zero-byte PNGs — don't cache them,
                    // the next request retries
                    if (new FileInfo(partPath).Length == 0)
                    {
                        File.Delete(partPath);
                        continue;
                    }

                    File.Move(partPath, path, overwrite: true);
                    logger.Information("Persona thumbnail {Id} fetched into the volume cache (panel lazy resolve)", imageId);
                    return path;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "Persona thumbnail {Id} fetch failed from {Url}", imageId, candidate);
                }
            }

            return null;
        }
        finally
        {
            ThumbnailGate.Release();
        }
    }
}
