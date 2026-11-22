using Serilog;
using System.Text.Json;
using System.Text.Json.Serialization;
using ILogger = Serilog.ILogger;
using CItem = Apace.StaticData.Playfab.Item;

namespace Apace.ApiServer.Services;

// The live persona catalog.
//
// The panel's LivePersonaCatalogSync (Apace.LauncherUI) walks the still-live
// PlayFab marketplace of the Minecraft Earth title (20ca2) — ~40k PersonaDurable
// entries, ~12k unique character creator pieces incl. emotes — and caches the
// metadata in the persistent data volume:
//
//   <mojang-assets>/persona-catalog/index.json          deduped per-piece summary (facts)
//   <mojang-assets>/persona-catalog/items/<id>.json     raw PlayFab item metadata (facts)
//
// Nothing from the catalog is ever committed to the repo: metadata are cached
// runtime facts in the volume, binaries are only downloaded on demand (below).
//
// This service is the ApiServer side: it turns the cached metadata into the
// catalog offer served to the client (Catalog/Search + Catalog/GetPublishedItem)
// and owns the lazy binary/thumbnail liveness machinery:
//
//   - Offer build ("unlock-all"): every offered item is a plain free item —
//     achievement lock tags stripped, price 0, purchasable true — mirroring what
//     update_apace_items.py did for the legacy 231 items. The strip happens on
//     the OUTPUT only; the raw cache keeps the live metadata verbatim.
//   - Lazy probe: the client never sees a live CDN URL — the offer advertises
//     /playfab/persona/<itemId>.zip (PersonaCdnController). On the first client
//     request for a piece binary the controller resolves the real URL
//     (Catalog/GetItem — Search never returns Contents), HEAD-probes it
//     (404 → the same GUID re-hosted on the new PlayFab content CDN
//     content1/content2.prod.catalog.playfab.com → else dead), downloads the
//     piece into <mojang-assets>/persona-catalog/binaries/ and serves it.
//     Dead items leave the offer until they come back alive.
//   - Thumbnails are resolved the same lazy way by MojangImagesCdnController
//     (cache → live CDN URL → Bedrock-Persona mirror URL → 404).
//
// There is deliberately NO bulk probing: the only remote calls are the lazy,
// client-driven ones (plus the panel's own sync), all funneled through a shared
// ≤2 req/s pacer with per-request retry/backoff.
public static class LivePersonaCatalog
{
    public const string CatalogDirName = "persona-catalog";
    public const string IndexFileName = "index.json";
    public const string ProbeStateFileName = "probe-state.json";
    public const string ItemsDirName = "items";
    public const string BinariesDirName = "binaries";
    public const string ThumbnailsDirName = "thumbnails";

    // MCE PlayFab namespace — the path segment of the persona asset URLs.
    public const string MceNamespace = "b63a0803d3653643";
    public const string PlayFabApi = "https://20ca2.playfabapi.com";
    public const string Scid = "4fc10100-5f7a-4470-899b-280835760c07";

    // The PlayFab content CDN hosts the same namespace/<guid>/primary.zip path
    // the old xforge CDN served — the 22.11 probe found new catalog items alive
    // there while the xforge originals are dark (generate_mojang_asset_manifest.py
    // probes the same shapes for the manifest).
    public static readonly string[] AlternateCdnHosts =
    [
        "content1.prod.catalog.playfab.com",
        "content2.prod.catalog.playfab.com",
    ];

    // Runtime thumbnail mirror: raw.githubusercontent.com serves the mirrored
    // Bedrock persona images (URL fact only — nothing is committed to the repo).
    public const string ThumbnailMirrorBase = "https://raw.githubusercontent.com/Daniel-Ric/Bedrock-Persona/main/images";

    // unlock-all: tags the client reads as achievement locks (the MCE client shows
    // those items as locked behind achievements and refuses to acquire them —
    // same list scripts/update_apace_items.py stripped from the legacy items).
    public static readonly string[] LockTags =
    [
        "is_achievement",
        "earth_achievement",
        "vanilla_achievement",
    ];

    // hidden_offer items never appeared in the dressing room (StoreConfig
    // globalNotTags excludes them from every query) — keep it that way.
    public const string HiddenOfferTag = "hidden_offer";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim StateLock = new(1, 1);

    public static string CatalogDir => Path.Combine(Program.MojangAssetsDir, CatalogDirName);
    public static string IndexPath => Path.Combine(CatalogDir, IndexFileName);
    public static string ProbeStatePath => Path.Combine(CatalogDir, ProbeStateFileName);
    public static string ItemsDir => Path.Combine(CatalogDir, ItemsDirName);
    public static string BinariesDir => Path.Combine(CatalogDir, BinariesDirName);
    public static string ThumbnailsDir => Path.Combine(CatalogDir, ThumbnailsDirName);

    /// <summary>Panel toggle (--live-persona-catalog); the consent gate stays the asset gate's.</summary>
    public static bool Enabled { get; set; }

    /// <summary>True when enabled AND a usable synced index exists in the volume.</summary>
    public static bool IsAvailable => Enabled && Index is not null;

    /// <summary>Index stamp — changes on every re-sync; offer caches key off it.</summary>
    public static long IndexStamp { get; private set; }

    public static DateTimeOffset? LastSyncAt => Index?.SyncedAt;
    public static int IndexedItemCount => Index?.Items.Count ?? 0;
    public static int EmoteCount => Index?.EmoteCount ?? 0;

    // ---------------------------------------------------------------------------
    // Index loading (hot-reloads when the panel re-syncs)
    // ---------------------------------------------------------------------------

    public sealed record LiveIndexItem(
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
        List<LiveItemReference> ItemReferences,
        string? ThumbnailId,
        string? ThumbnailUrl,
        DateTime CreationDate,
        DateTime LastModifiedDate,
        DateTime? StartDate,
        bool IsEmote)
    {
        [JsonIgnore]
        public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailId);
    }

    public sealed record LiveItemReference(Guid Id, int Amount);

    public sealed record LiveIndex(DateTimeOffset? SyncedAt, int RawItemCount, int EmoteCount, List<LiveIndexItem> Items);

    private static LiveIndex? _index;

    private static LiveIndex? Index
    {
        get
        {
            if (!Enabled)
            {
                return null;
            }

            try
            {
                if (File.Exists(IndexPath))
                {
                    long stamp = File.GetLastWriteTimeUtc(IndexPath).Ticks;
                    if (_index is null || stamp != IndexStamp)
                    {
                        var loaded = JsonSerializer.Deserialize<LiveIndex>(File.ReadAllText(IndexPath), JsonOptions);
                        if (loaded?.Items is { Count: > 0 })
                        {
                            _index = loaded;
                            IndexStamp = stamp;
                            Log.Information(
                                "Live persona catalog index loaded from {Path} — {Unique} unique piece(s) from {Raw} raw item(s), emotes: {Emotes}, synced {Synced}",
                                IndexPath, loaded.Items.Count, loaded.RawItemCount, loaded.EmoteCount, loaded.SyncedAt);
                        }
                        else
                        {
                            Log.Warning("Live persona catalog index at {Path} has no items — live offer disabled", IndexPath);
                        }
                    }
                }
                else if (_index is not null)
                {
                    Log.Information("Live persona catalog index gone from {Path} — live offer disabled", IndexPath);
                    _index = null;
                    IndexStamp = 0;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not load the live persona catalog index from {Path}", IndexPath);
            }

            return _index;
        }
    }

    // ---------------------------------------------------------------------------
    // The offer (unlock-all strip applied here, never to the raw cache)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The cached metadata as catalog items ready for the client: one item per
    /// persona piece (deduped by the sync, defensively here too), emotes skipped
    /// (see docs/live-persona-catalog.md — the MCE dressing room layout has no
    /// emote category), achievement/lock fields neutralized, Contents pointing at
    /// the local persona proxy. Items probed dead drop out until they revive.
    /// </summary>
    public static IReadOnlyList<CItem> GetOfferItems()
    {
        var index = Index;
        if (index is null)
        {
            return [];
        }

        var dead = GetDeadItemIds();
        var items = new List<CItem>(index.Items.Count);
        var seenPieces = new HashSet<Guid>();
        foreach (var entry in index.Items)
        {
            if (entry.IsEmote || dead.Contains(entry.Id))
            {
                continue;
            }

            if (!seenPieces.Add(entry.PieceId))
            {
                continue;
            }

            items.Add(BuildOfferItem(entry));
        }

        return items;
    }

    public static CItem? GetOfferItem(string itemId)
    {
        var index = Index;
        if (index is null)
        {
            return null;
        }

        var entry = index.Items.FirstOrDefault(item => item.Id == itemId);
        return entry is not null && !entry.IsEmote && !GetDeadItemIds().Contains(entry.Id) ? BuildOfferItem(entry) : null;
    }

    /// <summary>unlock-all: the offer item is a plain free item, whatever the marketplace says.</summary>
    public static CItem BuildOfferItem(LiveIndexItem entry)
    {
        // Tags: drop the achievement lock tags verbatim; hidden_offer entries were
        // already dropped by the sync (belt and braces here).
        var tags = entry.Tags.Where(tag => !LockTags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();

        return new CItem(
            true, // Purchasable — reward-only items were not acquirable; every offer is claimable
            new CItem.PersonaData(
                entry.PieceType,
                entry.PieceId,
                entry.OfferId ?? entry.PieceId,
                entry.PieceVersion,
                entry.Rarity,
                0, // Price — 0 = free
                entry.CreatorName ?? "Minecraft",
                null // AssetFileName — binaries ride the Contents personabinary entry below
            ),
            entry.Title,
            string.IsNullOrWhiteSpace(entry.Description) ? "§" : entry.Description,
            entry.ThumbnailId,
            entry.CreationDate,
            entry.LastModifiedDate,
            entry.StartDate ?? entry.CreationDate,
            Guid.Parse(entry.Id),
            entry.OfferId ?? entry.PieceId,
            "B63A0803D3653643",
            entry.CreatorEntityId ?? "301F442C3B63DC20",
            tags,
            entry.Keywords?.Count is > 0
                ? entry.Keywords.ToDictionary(kv => kv.Key, kv => new CItem.KeywordValues(kv.Value))
                : new Dictionary<string, CItem.KeywordValues>(),
            [
                // the client downloads every piece from the item's personabinary
                // content — always through OUR proxy path; the real URL is resolved
                // server-side on the first request (lazy probe below)
                new CItem.QueryManifestContent(
                    $"{entry.PieceId}_personabinary",
                    $"/playfab/persona/{entry.Id}.zip",
                    "65535.65535.65535",
                    "1.13.0",
                    [],
                    "personabinary"
                ),
            ],
            entry.ItemReferences.Select(reference => new CItem.ItemReference(reference.Id, reference.Amount)).ToList(),
            new Dictionary<string, string>(),
            new Dictionary<string, string>()
        );
    }

    // ---------------------------------------------------------------------------
    // Probe state (lazy liveness cache)
    // ---------------------------------------------------------------------------

    public sealed record ProbeEntry(
        string State, // "live" | "dead"
        string? ResolvedUrl,
        DateTimeOffset CheckedAt,
        string? Source);

    private sealed class ProbeStateFile
    {
        public Dictionary<string, ProbeEntry> Items { get; set; } = [];
    }

    private static readonly Dictionary<string, ProbeEntry> ProbeStates = [];
    private static bool _probeStatesLoaded;
    private static bool _probeStateDirty;
    private static DateTime _probeStateLastSave = DateTime.MinValue;

    private static void LoadProbeStates()
    {
        if (_probeStatesLoaded)
        {
            return;
        }

        _probeStatesLoaded = true;
        try
        {
            if (!File.Exists(ProbeStatePath))
            {
                return;
            }

            var file = JsonSerializer.Deserialize<ProbeStateFile>(File.ReadAllText(ProbeStatePath), JsonOptions);
            if (file?.Items is not null)
            {
                foreach (var (itemId, entry) in file.Items)
                {
                    ProbeStates[itemId] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the live persona probe state from {Path} — every item starts unchecked", ProbeStatePath);
        }
    }

    // A dead verdict from BEFORE the last-but-one sync is not trusted forever — a
    // Mojang CDN migration can revive pieces, so stale dead entries (older than
    // 24h) fall back to unchecked and get re-probed on the next client request.
    private static bool IsStaleDead(ProbeEntry entry)
        => entry.State == "dead" && entry.CheckedAt < (LastSyncAt ?? DateTimeOffset.UtcNow.AddDays(-1)) - TimeSpan.FromHours(24);

    /// <summary>Item ids the offer must hide (probed dead, verdict still fresh).</summary>
    public static HashSet<string> GetDeadItemIds()
    {
        lock (StateLock)
        {
            LoadProbeStates();
            return ProbeStates
                .Where(kv => kv.Value.State == "dead" && !IsStaleDead(kv.Value))
                .Select(kv => kv.Key)
                .ToHashSet();
        }
    }

    // Panel counter: live / dead / unchecked.
    public static (int Live, int Dead, int Unchecked, int CachedBinaries, int CachedThumbnails) GetProbeSummary()
    {
        lock (StateLock)
        {
            LoadProbeStates();
            int live = 0, dead = 0;
            foreach (var entry in ProbeStates.Values)
            {
                if (IsStaleDead(entry))
                {
                    continue;
                }

                if (entry.State == "live")
                {
                    live++;
                }
                else if (entry.State == "dead")
                {
                    dead++;
                }
            }

            int cachedBinaries = 0;
            if (Directory.Exists(BinariesDir))
            {
                cachedBinaries = Directory.EnumerateFiles(BinariesDir, "*.zip").Count();
            }

            int cachedThumbnails = 0;
            if (Directory.Exists(ThumbnailsDir))
            {
                cachedThumbnails = Directory.EnumerateFiles(ThumbnailsDir).Count();
            }

            return (live, dead, Math.Max(0, IndexedItemCount - live - dead), cachedBinaries, cachedThumbnails);
        }
    }

    private static void SaveProbeStates(ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(CatalogDir);
            string tempPath = ProbeStatePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new ProbeStateFile { Items = ProbeStates }, JsonOptions));
            File.Move(tempPath, ProbeStatePath, overwrite: true);
            _probeStateDirty = false;
            _probeStateLastSave = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Could not save the live persona probe state to {Path}", ProbeStatePath);
        }
    }

    // Probe state writes are per-item and can burst (a player browsing a category);
    // persist at most every 15 seconds so the bursts do not rewrite the file constantly.
    private static void MarkProbeStateDirty(ILogger logger)
    {
        _probeStateDirty = true;
        if ((DateTime.UtcNow - _probeStateLastSave).TotalSeconds >= 15)
        {
            SaveProbeStates(logger);
        }
    }

    /// <summary>Flushes a pending probe-state write (called from the controllers after bursts).</summary>
    public static void FlushProbeStates(ILogger logger)
    {
        lock (StateLock)
        {
            if (_probeStateDirty)
            {
                SaveProbeStates(logger);
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Remote access: pacer + anonymous PlayFab login + URL discovery
    // ---------------------------------------------------------------------------

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly SemaphoreSlim RemoteGate = new(1, 1);
    private static DateTime _lastRemoteCall = DateTime.MinValue;
    private static readonly SemaphoreSlim LoginLock = new(1, 1);

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        _ = client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "KotPasztet/Apace live-persona-catalog");
        return client;
    }

    // Polite limiter for every remote call this service makes (probe, GetItem
    // discovery, binary/thumbnail download): at most ~2 requests per second.
    // There are no bulk loops anywhere — the only callers are lazy, client-driven.
    private static async Task PaceRemoteAsync(CancellationToken cancellationToken)
    {
        await RemoteGate.WaitAsync(cancellationToken);
        try
        {
            TimeSpan wait = TimeSpan.FromMilliseconds(500) - (DateTime.UtcNow - _lastRemoteCall);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            _lastRemoteCall = DateTime.UtcNow;
        }
        finally
        {
            RemoteGate.Release();
        }
    }

    private static string? _masterToken;
    private static DateTimeOffset _masterTokenIssued;

    // Anonymous AndroidDeviceID login (same flow as scripts/fetch_persona_catalog.py):
    // client entity token exchanged for a master_player_account token, the only kind
    // Catalog/Search and Catalog/GetItem accept. Re-issued after 12h (PlayFab entity
    // tokens live ~24h) or when PlayFab answers with an auth error.
    private static async Task<string> GetMasterTokenAsync(ILogger logger, CancellationToken cancellationToken)
    {
        if (_masterToken is not null && DateTimeOffset.UtcNow - _masterTokenIssued < TimeSpan.FromHours(12))
        {
            return _masterToken;
        }

        await LoginLock.WaitAsync(cancellationToken);
        try
        {
            if (_masterToken is not null && DateTimeOffset.UtcNow - _masterTokenIssued < TimeSpan.FromHours(12))
            {
                return _masterToken;
            }

            await PaceRemoteAsync(cancellationToken);
            var loginBody = JsonSerializer.Serialize(new
            {
                TitleId = "20CA2",
                AndroidDeviceId = $"apace-{Environment.MachineName}",
                AndroidDevice = "Apace",
                OS = "Linux",
                CreateAccount = true,
            });
            using var loginResponse = await HttpClient.PostAsync(
                $"{PlayFabApi}/Client/LoginWithAndroidDeviceID",
                new StringContent(loginBody, System.Text.Encoding.UTF8, "application/json"), cancellationToken);
            loginResponse.EnsureSuccessStatusCode();
            using var loginDoc = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync(cancellationToken));
            string clientToken = loginDoc.RootElement.GetProperty("data").GetProperty("EntityToken").GetProperty("EntityToken").GetString()
                ?? throw new InvalidOperationException("login returned no entity token");
            string playFabId = loginDoc.RootElement.GetProperty("data").GetProperty("PlayFabId").GetString() ?? "";

            await PaceRemoteAsync(cancellationToken);
            var exchangeBody = JsonSerializer.Serialize(new
            {
                Entity = new { Id = playFabId, Type = "master_player_account" },
            });
            using var exchangeRequest = new HttpRequestMessage(HttpMethod.Post, $"{PlayFabApi}/Authentication/GetEntityToken")
            {
                Content = new StringContent(exchangeBody, System.Text.Encoding.UTF8, "application/json"),
            };
            exchangeRequest.Headers.Add("X-EntityToken", clientToken);
            using var exchangeResponse = await HttpClient.SendAsync(exchangeRequest, cancellationToken);
            exchangeResponse.EnsureSuccessStatusCode();
            using var exchangeDoc = JsonDocument.Parse(await exchangeResponse.Content.ReadAsStringAsync(cancellationToken));
            _masterToken = exchangeDoc.RootElement.GetProperty("data").GetProperty("EntityToken").GetRawText().Trim('"');
            _masterTokenIssued = DateTimeOffset.UtcNow;
            logger.Information("Live persona catalog: logged in to PlayFab 20ca2 anonymously ({PlayFabId})", playFabId);
            return _masterToken;
        }
        finally
        {
            LoginLock.Release();
        }
    }

    private static void InvalidateMasterToken() => _masterToken = null;

    /// <summary>
    /// Catalog/GetItem — the only endpoint that returns the real personabinary
    /// Contents entry (Search never does). Returns Contents[0].Url or null.
    /// </summary>
    public static async Task<string?> ResolveBinaryUrlAsync(string itemId, ILogger logger, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                string token = await GetMasterTokenAsync(logger, cancellationToken);
                await PaceRemoteAsync(cancellationToken);
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{PlayFabApi}/Catalog/GetItem")
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new { Id = itemId, Scid = Scid }),
                        System.Text.Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-EntityToken", token);
                using var response = await HttpClient.SendAsync(request, cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    InvalidateMasterToken();
                    throw new HttpRequestException($"auth rejected with HTTP {(int)response.StatusCode}");
                }

                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("Item", out var item) ||
                    !item.TryGetProperty("Contents", out var contents) ||
                    contents.ValueKind != JsonValueKind.Array ||
                    contents.GetArrayLength() == 0)
                {
                    return null; // no contents at all — the item has no downloadable binary
                }

                string? url = contents[0].TryGetProperty("Url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String
                    ? urlProp.GetString()
                    : null;
                return string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? null : url;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3)
            {
                logger.Warning("Live persona catalog GetItem attempt {Attempt}/3 failed for {Item}: {Reason}", attempt, itemId, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }
    }

    /// <summary>The same payload on the PlayFab content CDN (content1/content2).</summary>
    public static IEnumerable<string> AlternateCdnUrls(string url)
    {
        static bool Guidish(string segment)
            => segment.Length is >= 32 and <= 36 && segment.All(c => char.IsAsciiHexDigit(c) || c == '-');

        if (url.Contains("/pf-namespace-", StringComparison.Ordinal))
        {
            string tail = url.Split("/pf-namespace-", 2)[1];
            if (tail.Length == 0 || tail.Contains("://", StringComparison.Ordinal))
            {
                yield break;
            }

            foreach (string host in AlternateCdnHosts)
            {
                yield return $"https://{host}/pf-namespace-{tail}";
            }
        }
        else if (url.Contains("/pf-title-", StringComparison.Ordinal))
        {
            string[] segments = [.. url.Split('/', StringSplitOptions.RemoveEmptyEntries)];
            if (segments.Length >= 2 && Guidish(segments[^2]))
            {
                string tail = $"{segments[^2]}/{segments[^1]}";
                foreach (string host in AlternateCdnHosts)
                {
                    yield return $"https://{host}/pf-namespace-{MceNamespace}/{tail}";
                }
            }
        }
    }

    // HEAD (fallback GET range 0-0) — the xforge/content CDNs do not always honour
    // HEAD, so the range GET is the authoritative fallback.
    private static async Task<int?> ProbeUrlAsync(string url, CancellationToken cancellationToken)
    {
        await PaceRemoteAsync(cancellationToken);
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
        try
        {
            using var headResponse = await HttpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)headResponse.StatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // HEAD not honoured / transport error — retry as a 1-byte GET
        }

        try
        {
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
            getRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var getResponse = await HttpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)getResponse.StatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null; // unreachable
        }
    }

    // ---------------------------------------------------------------------------
    // Lazy binary ensure (PersonaCdnController miss path)
    // ---------------------------------------------------------------------------

    private static readonly Dictionary<string, SemaphoreSlim> ItemLocks = [];
    private static bool _binariesLayoutCreated;

    private static SemaphoreSlim GetItemLock(string itemId)
    {
        lock (StateLock)
        {
            if (!ItemLocks.TryGetValue(itemId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                ItemLocks[itemId] = gate;
            }

            return gate;
        }
    }

    public static string BinaryPath(string itemId) => Path.Combine(BinariesDir, $"{itemId}.zip");

    /// <summary>
    /// The client asked for a live piece binary: serve from cache, else run the
    /// lazy pipeline — resolve the URL (GetItem), probe it (404 → alternate CDN
    /// shapes → dead), download to the volume cache. Returns false when the item
    /// is dead (the controller answers 404 and the item leaves the offer).
    /// </summary>
    public static async Task<bool> TryEnsureBinaryAsync(string itemId, ILogger logger, CancellationToken cancellationToken)
    {
        var gate = GetItemLock(itemId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (StateLock)
            {
                LoadProbeStates();
            }

            string path = BinaryPath(itemId);
            if (ProbeStates.TryGetValue(itemId, out var state))
            {
                if (state.State == "live" && File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    return true; // cached from an earlier request
                }

                if (state.State == "dead" && !IsStaleDead(state))
                {
                    return false; // freshly probed dead — nothing to fetch
                }
            }

            if (!_binariesLayoutCreated)
            {
                Directory.CreateDirectory(BinariesDir);
                _binariesLayoutCreated = true;
            }

            // 1. resolve the real URL (Search never returns Contents; GetItem does)
            string? url = await ResolveBinaryUrlAsync(itemId, logger, cancellationToken);
            if (url is null)
            {
                RecordProbe(itemId, "dead", null, "no-contents", logger);
                return false;
            }

            // 2. probe: primary first, then the same GUID on the new content CDN
            int? status = await ProbeUrlAsync(url, cancellationToken);
            string source = "primary";
            string resolved = url;
            if (status is not (200 or 206))
            {
                foreach (string alternate in AlternateCdnUrls(url))
                {
                    int? alternateStatus = await ProbeUrlAsync(alternate, cancellationToken);
                    if (alternateStatus is 200 or 206)
                    {
                        resolved = alternate;
                        source = alternate.Split('/')[2];
                        status = alternateStatus;
                        break;
                    }
                }
            }

            if (status is not (200 or 206))
            {
                RecordProbe(itemId, "dead", url, $"probe-{status ?? 0}", logger);
                logger.Information("Live persona piece {Item} probed dead (primary {Primary}, alternates tried) — dropped from the offer until it revives",
                    itemId, status ?? 0);
                return false;
            }

            RecordProbe(itemId, "live", resolved, source, logger);

            // 3. download into the volume cache (retry/backoff like the asset gate)
            await DownloadBinaryAsync(resolved, path, logger, cancellationToken);
            logger.Information("Live persona piece {Item} downloaded from {Source} into the volume cache", itemId, source);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private static void RecordProbe(string itemId, string state, string? resolvedUrl, string source, ILogger logger)
    {
        lock (StateLock)
        {
            ProbeStates[itemId] = new ProbeEntry(state, resolvedUrl, DateTimeOffset.UtcNow, source);
            MarkProbeStateDirty(logger);
        }
    }

    private static async Task DownloadBinaryAsync(string url, string destPath, ILogger logger, CancellationToken cancellationToken)
    {
        string partPath = destPath + ".part";
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await PaceRemoteAsync(cancellationToken);
                using var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using (var content = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await content.CopyToAsync(fileStream, cancellationToken);
                }

                File.Move(partPath, destPath, overwrite: true);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 3)
            {
                logger.Warning("Live persona binary download attempt {Attempt}/3 failed from {Url}: {Reason}", attempt, url, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }

        // 3 attempts exhausted — the exception from the last attempt propagates
        // to the controller, which answers 404; the probe verdict stays live so
        // a transient CDN hiccup does not kill the item (the next request retries).
    }

    /// <summary>The resolved URL for a live item, if it already probed alive.</summary>
    public static string? GetResolvedUrl(string itemId)
    {
        lock (StateLock)
        {
            LoadProbeStates();
            return ProbeStates.TryGetValue(itemId, out var entry) && entry.State == "live" ? entry.ResolvedUrl : null;
        }
    }

    // ---------------------------------------------------------------------------
    // Lazy thumbnail ensure (MojangImagesCdnController miss path)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The dressing room asked for a live item thumbnail: serve from cache, else
    /// download from the thumbnail URL recorded at sync time; on failure try the
    /// Bedrock-Persona mirror. Returns the cached file path or null.
    /// </summary>
    public static async Task<string?> TryEnsureThumbnailAsync(string imageId, ILogger logger, CancellationToken cancellationToken)
    {
        var index = Index;
        if (index is null)
        {
            return null;
        }

        var entry = index.Items.FirstOrDefault(item => item.ThumbnailId == imageId);
        if (entry is null)
        {
            return null; // not a live catalog thumbnail
        }

        var gate = GetItemLock($"thumb:{imageId}");
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(ThumbnailsDir);
            string path = Path.Combine(ThumbnailsDir, $"{imageId}.png");
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return path;
            }

            string mirrorUrl = $"{ThumbnailMirrorBase}/{entry.PieceType}/{imageId}.png";
            foreach (string candidate in new[] { entry.ThumbnailUrl, mirrorUrl }.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct())
            {
                try
                {
                    await PaceRemoteAsync(cancellationToken);
                    using var response = await HttpClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

                    File.Move(partPath, path, overwrite: true);
                    return path;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.Information("Live persona thumbnail {Image} could not be fetched from {Url}: {Reason}", imageId, candidate, ex.Message);
                }
            }

            return null;
        }
        finally
        {
            gate.Release();
        }
    }
}
