using Serilog;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ILogger = Serilog.ILogger;

namespace Apace.LauncherUI.Services;

/// <summary>One entry of <c>mojang-assets.manifest.json</c>.</summary>
public sealed record MojangAssetManifestItem
{
    /// <summary>"persona" | "thumbnail" | "skin-texture".</summary>
    public string Kind { get; init; } = "";

    /// <summary>Relative target path inside the Mojang assets dir, e.g. "playfab/persona/&lt;id&gt;.zip".</summary>
    public string File { get; init; } = "";

    /// <summary>Source URL the file is downloaded from (a recorded fact, may be offline).</summary>
    public string Url { get; init; } = "";

    /// <summary>
    /// Mirror locations tried in order when the primary URL fails (non-200 or
    /// unreachable). The payload must still match the recorded sha256/size, so
    /// a mirror is exactly as trustworthy as the primary source.
    /// </summary>
    public List<string>? FallbackUrls { get; init; }

    /// <summary>Known sha256 of the file (hex), when it could be verified at manifest generation time.</summary>
    public string? Sha256 { get; init; }

    public long? Size { get; init; }

    /// <summary>
    /// True when the URL was verified reachable AND the payload hash confirmed when the manifest
    /// was generated. Only verified entries are downloaded and only verified entries block startup.
    /// Unverified entries are recorded facts with no reachable source — they are surfaced as
    /// "unavailable" in the panel but never block.
    /// </summary>
    public bool Verified { get; init; }

    /// <summary>Optional display name (used for default skin textures).</summary>
    public string? Name { get; init; }
}

public sealed record MojangAssetManifest
{
    public int Version { get; init; }
    public string? GeneratedAt { get; init; }
    public Dictionary<string, string>? Sources { get; init; }
    public List<MojangAssetManifestItem> Items { get; init; } = [];
}

/// <summary>Download progress for the panel UI (files, not bytes — the manifest is many small files).</summary>
public sealed record MojangAssetDownloadProgress(int DownloadedFiles, int TotalFiles, string? CurrentFile);

/// <summary>Aggregated view for the consent alert on the Server Status page.</summary>
public sealed record MojangAssetGateSummary(
    int PersonaCount,
    int ThumbnailCount,
    int SkinTextureCount,
    int DownloadFileCount,
    long DownloadBytes,
    int UnavailableCount,
    IReadOnlyList<string> Hosts,
    string AssetsDir,
    string? ManifestVersion,
    bool ConsentRecorded);

// The Mojang asset gate.
//
// The server cannot start without the additional Mojang assets (persona piece
// binaries, dressing room thumbnails, default character textures) that live in
// the persistent data volume (<DataDir>/mojang-assets/). The repo ships only
// the manifest (facts: source URLs, sha256, sizes) — never the assets.
//
// State machine (mirrors the EULA / resource pack prompts):
//  - EnsureReadyAsync is called from both startup paths (FileChecker.CheckAsync
//    for the full Start flow and ServerManager.EnsureComponentsOnline for
//    page-level component starts). It loads the manifest, compares it against
//    the assets dir and either passes, silently auto-redownloads (consent
//    already recorded) or arms the pending prompt (no consent yet).
//  - Pending exposes the armed prompt to the panel; AcceptMojangAssetsAndDownloadAsync
//    records the one-time consent (consent.json) and downloads with progress.
//    The gate's TaskCompletionSource is completed ONLY when every verified
//    asset is in place, so a failed download keeps startup blocked and the
//    alert offers Retry.
//  - A consent flag with missing files (fresh volume seeded elsewhere, manual
//    deletion, redeploy) auto-redownloads silently — the prompt is shown only
//    when that download fails or when there was never a consent.
//  - RequireMojangAssets = false (Server Options) disables the gate entirely.
//
// After the assets become complete, MojangAssetPostProcessor.RunAll runs
// (idempotently) — that hook belongs to later pipeline stages.
public static class MojangAssetGate
{
    private const string ManifestFileName = "mojang-assets.manifest.json";
    private const string AssetsDirName = "mojang-assets";
    private const string ConsentFileName = "consent.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim DownloadLock = new(1, 1);
    private static readonly object StateLock = new();

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static MojangAssetManifest? _manifest;
    private static string? _manifestPath;
    private static TaskCompletionSource? _pendingTcs;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        _ = client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"KotPasztet/Apace/{Assembly.GetExecutingAssembly().GetName().Version}");
        return client;
    }

    /// <summary>Persistent directory holding the assets (<c>/app/data/mojang-assets</c> in docker).</summary>
    public static string MojangAssetsDir => Path.Combine(Program.DataDir, AssetsDirName);

    /// <summary>One-time acceptance flag inside the assets dir (survives redeploys with the volume).</summary>
    public static string ConsentPath => Path.Combine(MojangAssetsDir, ConsentFileName);

    public static string ManifestPath => _manifestPath ?? "";

    public static MojangAssetManifest? Manifest => _manifest;
    public static bool ManifestLoaded => _manifest is not null;

    /// <summary>The settings toggle (Server Options). Default: gate ON.</summary>
    public static bool GateEnabled => Settings.Instance.RequireMojangAssets is not false;

    /// <summary>True while the consent prompt is armed (blocking startup or a failed download waits for Retry).</summary>
    public static bool Pending => _pendingTcs is not null && !_pendingTcs.Task.IsCompleted;

    /// <summary>The gate's completion source; completed only when the assets are complete.</summary>
    public static Task? CompletionTask => _pendingTcs?.Task;

    public static bool HasConsent => File.Exists(ConsentPath);

    // Download state (panel UI reads these while polling).
    public static bool IsDownloading { get; private set; }
    public static int DownloadedCount { get; private set; }
    public static int TotalCount { get; private set; }
    public static string? CurrentFile { get; private set; }
    public static IReadOnlyList<string> LastErrors { get; private set; } = [];

    // ---------------------------------------------------------------------------
    // Manifest loading
    // ---------------------------------------------------------------------------

    private static IEnumerable<string> ManifestPathCandidates()
    {
        string[] relative =
        [
            // Deployment (docker image / publish): staticdata-extras content is
            // overlaid onto staticdata, which sits next to the panel's working dir.
            "../staticdata/playfab/" + ManifestFileName,
            // Dev checkouts: the manifest is committed under staticdata-extras.
            // Walk up until the repo root's copy matches (e.g. a working directory
            // or AppContext.BaseDirectory of bin/Debug/net10.0 is five levels deep).
            "../../staticdata-extras/playfab/" + ManifestFileName,
            "../../../staticdata-extras/playfab/" + ManifestFileName,
            "../../../../staticdata-extras/playfab/" + ManifestFileName,
            "../../../../../staticdata-extras/playfab/" + ManifestFileName,
            // Custom staticdata location configured in settings.
            Path.Combine(Program.StaticDataDir, "playfab", ManifestFileName),
        ];

        // Relative candidates are resolved against BOTH anchors: the panel's
        // working directory (the deployment layout) and AppContext.BaseDirectory
        // (dev checkouts / launchers started with a foreign working directory).
        foreach (string candidate in relative)
        {
            yield return candidate;
            yield return Path.Combine(AppContext.BaseDirectory, candidate);
        }
    }

    /// <summary>Loads (and caches) the manifest, logging which candidate path won.</summary>
    public static bool TryLoadManifest(ILogger logger)
    {
        if (_manifest is not null)
        {
            return true;
        }

        foreach (string candidate in ManifestPathCandidates())
        {
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                var manifest = JsonSerializer.Deserialize<MojangAssetManifest>(File.ReadAllText(fullPath), JsonOptions);
                if (manifest?.Items is null || manifest.Items.Count == 0)
                {
                    logger.Warning("Mojang asset manifest at {Path} has no items — ignoring", fullPath);
                    continue;
                }

                _manifest = manifest;
                _manifestPath = fullPath;
                logger.Information("Mojang asset manifest loaded from {Path} ({Count} items, version {Version})", fullPath, manifest.Items.Count, manifest.Version);
                return true;
            }
            catch (Exception ex)
            {
                logger.Warning("Could not load Mojang asset manifest candidate {Path}: {Reason}", Path.GetFullPath(candidate), ex.Message);
            }
        }

        logger.Warning("Mojang asset manifest not found — searched: {Candidates}", string.Join(", ", ManifestPathCandidates().Select(Path.GetFullPath)));
        return false;
    }

    // The manifest resolver candidates point at the playfab directory that holds
    // the manifest — the same playfab directory (or its legacy staticdata twins)
    // holds playfab/persona, which the CDN controllers fall back to when the
    // manifest is unavailable.
    private static IEnumerable<string> PersonaDirCandidates()
    {
        foreach (string candidate in ManifestPathCandidates())
        {
            string? directory = Path.GetDirectoryName(candidate);
            if (directory is null)
            {
                continue;
            }

            yield return Path.Combine(directory, "persona");
        }
    }

    /// <summary>
    /// True when the legacy staticdata layout still has persona piece binaries
    /// (pre-gate installs / dev checkouts where staticdata-extras is committed).
    /// Used as the fallback signal when the manifest itself is unavailable.
    /// </summary>
    private static bool LegacyPersonaAssetsPresent(ILogger logger)
    {
        foreach (string candidate in PersonaDirCandidates().Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                if (Directory.EnumerateFiles(candidate, "*.zip").Any())
                {
                    logger.Information("Legacy persona assets found in {Dir} — the CDN controllers can serve them while the manifest is unavailable", candidate);
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Debug("Could not inspect persona directory {Dir}: {Reason}", candidate, ex.Message);
            }
        }

        return false;
    }

    private static bool IsManifestFileSafe(string file)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Contains("..") || Path.IsPathRooted(file))
        {
            return false;
        }

        string normalized = file.Replace('\\', '/');
        // no absolute or empty segments ("/a//b", "/a")
        return !normalized.StartsWith('/') && normalized.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    public static string GetAssetPath(string manifestFile)
        => Path.GetFullPath(Path.Combine(MojangAssetsDir, manifestFile));

    // ---------------------------------------------------------------------------
    // Completeness checks
    // ---------------------------------------------------------------------------

    private static string? ComputeSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when the asset exists in the assets dir and matches its recorded sha256 (when known).</summary>
    public static bool IsAssetPresent(MojangAssetManifestItem item)
    {
        string path = GetAssetPath(item.File);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            return false;
        }

        if (item.Sha256 is null)
        {
            // Nothing to verify against — presence counts (size mismatch is
            // tolerated for unverified entries; there is no trusted reference).
            return true;
        }

        return string.Equals(ComputeSha256(path), item.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Assets missing from the assets dir. By default only VERIFIED entries are
    /// reported (and only those block startup); pass includeUnverified to also
    /// list the entries whose source was offline at manifest generation time.
    /// </summary>
    public static List<MojangAssetManifestItem> GetMissingFiles(bool includeUnverified = false)
    {
        var manifest = _manifest;
        if (manifest is null)
        {
            return [];
        }

        var missing = new List<MojangAssetManifestItem>();
        foreach (var item in manifest.Items)
        {
            if (!IsManifestFileSafe(item.File))
            {
                continue;
            }

            if (!item.Verified && !includeUnverified)
            {
                continue;
            }

            if (!IsAssetPresent(item))
            {
                missing.Add(item);
            }
        }

        return missing;
    }

    /// <summary>Assets with no reachable source at manifest generation time (informational, never blocking).</summary>
    public static List<MojangAssetManifestItem> GetUnavailableFiles()
        => [.. GetMissingFiles(includeUnverified: true).Where(item => !item.Verified)];

    /// <summary>True when nothing verified is missing (or the manifest could not be loaded — the gate cannot judge).</summary>
    public static bool AreAssetsComplete()
        => _manifest is null || GetMissingFiles().Count == 0;

    // ---------------------------------------------------------------------------
    // The gate
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Single entry point for both startup paths. Returns true when startup may
    /// proceed (gate disabled, manifest unusable, or assets complete); false
    /// otherwise — in that case Pending tells the panel whether the consent
    /// prompt is armed.
    /// </summary>
    public static async Task<bool> EnsureReadyAsync(Settings settings, ILogger logger, CancellationToken cancellationToken)
    {
        if (settings.RequireMojangAssets is false)
        {
            CompletePending();
            return true;
        }

        if (!TryLoadManifest(logger))
        {
            // No manifest (not committed yet, stripped image, exotic working dir).
            // Policy (docs/mojang-asset-gate.md): fall back to the legacy layout —
            // when the staticdata persona directory still has content the CDN
            // controllers can serve it, so the start proceeds with a loud warning;
            // with NEITHER manifest NOR legacy persona assets the start is blocked
            // with a clear message (fail-closed — mirrors the ApiServer fail-fast).
            if (LegacyPersonaAssetsPresent(logger))
            {
                logger.Warning(
                    "Mojang asset manifest not found — proceeding on the legacy staticdata persona assets; the gate cannot verify completeness " +
                    "(generate mojang-assets.manifest.json to enforce it, or disable the gate in Server Options)");
                return true;
            }

            logger.Error(
                "Additional Mojang assets cannot be enforced — manifest not found and no legacy persona assets in staticdata — server startup blocked " +
                "(generate mojang-assets.manifest.json or disable the gate in Server Options)");
            return false;
        }

        List<MojangAssetManifestItem> missing = GetMissingFiles();
        if (missing.Count == 0)
        {
            // Also releases a prompt armed earlier (e.g. assets restored manually
            // or by another process) so the panel alert does not stick around.
            CompletePending();
            await RunPostProcessorAsync(logger);
            return true;
        }

        if (!HasConsent)
        {
            ArmPending(logger, missing);
            return false;
        }

        // Consent exists but files are gone (fresh volume seeded elsewhere,
        // manual deletion, redeploy pruning the dir) — auto-heal silently.
        logger.Information("Mojang assets incomplete ({Count} missing) with consent recorded — auto-redownloading", missing.Count);
        bool downloaded = await DownloadMissingAsync(logger, progress: null, cancellationToken);
        if (downloaded)
        {
            await RunPostProcessorAsync(logger);
            return true;
        }

        ArmPending(logger, GetMissingFiles());
        return false;
    }

    private static void ArmPending(ILogger logger, List<MojangAssetManifestItem> missing)
    {
        lock (StateLock)
        {
            if (_pendingTcs is null || _pendingTcs.Task.IsCompleted)
            {
                _pendingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        var unavailable = GetUnavailableFiles();
        logger.Information(
            "Mojang asset gate armed: {Missing} file(s) missing ({Bytes} bytes) from {Dir}, consent recorded: {Consent}, unavailable (source offline): {Unavailable}",
            missing.Count, missing.Sum(item => item.Size ?? 0), MojangAssetsDir, HasConsent, unavailable.Count);
    }

    /// <summary>
    /// Panel "Accept & Download" / "Retry": records the one-time consent and
    /// downloads everything verified that is missing. Returns true only when
    /// the assets are complete afterwards; failures land in <see cref="LastErrors"/>.
    /// </summary>
    public static async Task<bool> AcceptMojangAssetsAndDownloadAsync(IProgress<MojangAssetDownloadProgress>? progress = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        logger ??= Log.Logger;
        WriteConsent(logger);
        bool complete = await DownloadMissingAsync(logger, progress, cancellationToken);
        return complete;
    }

    private static void WriteConsent(ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(MojangAssetsDir);
            if (File.Exists(ConsentPath))
            {
                return;
            }

            var consent = new ConsentData(DateTimeOffset.UtcNow, _manifest is null ? "unknown" : _manifest.Version.ToString(CultureInfo.InvariantCulture));
            File.WriteAllText(ConsentPath, JsonSerializer.Serialize(consent, JsonOptions));
            logger.Information("Mojang asset consent recorded at {Path} (manifest version {Version})", ConsentPath, consent.ManifestVersion);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Could not record Mojang asset consent at {Path}", ConsentPath);
        }
    }

    private sealed record ConsentData(DateTimeOffset AcceptedAt, string ManifestVersion);

    /// <summary>Downloads every verified asset missing from the assets dir. Never throws.</summary>
    public static async Task<bool> DownloadMissingAsync(ILogger logger, IProgress<MojangAssetDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!TryLoadManifest(logger))
        {
            LastErrors = ["Mojang asset manifest not found — cannot download assets"];
            return false;
        }

        if (!await DownloadLock.WaitAsync(TimeSpan.Zero, CancellationToken.None))
        {
            logger.Warning("A Mojang asset download is already in progress — it may have been started from another browser tab");
            return false;
        }

        IsDownloading = true;
        LastErrors = [];
        DownloadedCount = 0;
        try
        {
            var targets = GetMissingFiles();
            if (targets.Count == 0)
            {
                CompletePending();
                await RunPostProcessorAsync(logger);
                return true;
            }

            TotalCount = targets.Count;
            var errors = new List<string>();
            Directory.CreateDirectory(MojangAssetsDir);
            // lay out the full assets tree up front (incl. post-processor target dirs)
            foreach (string dir in AssetLayoutDirectories)
            {
                Directory.CreateDirectory(Path.Combine(MojangAssetsDir, dir));
            }

            foreach (var item in targets)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    errors.Add($"{item.File}: download cancelled");
                    break;
                }

                CurrentFile = item.File;
                progress?.Report(new MojangAssetDownloadProgress(DownloadedCount, TotalCount, item.File));
                try
                {
                    // Transient network failures (dead host, reset connection,
                    // truncated body) retry a few times before the file is
                    // reported as failed — a 200-file manifest must not need
                    // 200 manual Retry clicks.
                    const int MaxAttempts = 3;
                    for (int attempt = 1; ; attempt++)
                    {
                        try
                        {
                            await DownloadItemAsync(item, logger, cancellationToken);
                            break;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex) when (attempt < MaxAttempts)
                        {
                            logger.Warning("Mojang asset download attempt {Attempt}/{Max} failed for {File}: {Reason}", attempt, MaxAttempts, item.File, ex.Message);
                            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
                        }
                    }

                    DownloadedCount++;
                    progress?.Report(new MojangAssetDownloadProgress(DownloadedCount, TotalCount, item.File));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    errors.Add($"{item.File}: download cancelled");
                    break;
                }
                catch (Exception ex)
                {
                    errors.Add($"{item.File}: {ex.Message}");
                    logger.Warning("Mojang asset download failed for {File}: {Reason}", item.File, ex.Message);
                }
            }

            LastErrors = errors;

            var unavailable = GetUnavailableFiles();
            if (unavailable.Count > 0)
            {
                logger.Warning(
                    "{Count} additional Mojang asset(s) have no reachable source (offline at manifest generation time) and are not required to start: {Files}",
                    unavailable.Count, string.Join(", ", unavailable.Take(5).Select(item => item.File)) + (unavailable.Count > 5 ? ", ..." : ""));
            }

            bool complete = AreAssetsComplete();
            if (complete)
            {
                logger.Information("All required additional Mojang assets are in place in {Dir}", MojangAssetsDir);
                CompletePending();
                await RunPostProcessorAsync(logger);
            }
            else
            {
                logger.Error("Mojang asset download finished with {Failed} failure(s) — the gate stays closed (Retry available in the panel)", errors.Count);
            }

            return complete;
        }
        finally
        {
            IsDownloading = false;
            CurrentFile = null;
            DownloadLock.Release();
        }
    }

    private static void CompletePending()
    {
        lock (StateLock)
        {
            _pendingTcs?.TrySetResult();
        }
    }

    /// <summary>Layout of the assets dir; <c>playfab/skinpacks</c> and <c>skins</c> are post-processor targets.</summary>
    public static readonly string[] AssetLayoutDirectories =
    [
        "playfab/persona",
        "playfab/skinpacks",
        "images",
        "skins",
    ];

    // The primary URL first, then the entry's fallbackUrls in order — the first
    // source that answers 200 wins and the rest are not contacted. A candidate
    // failure (non-200 or transport error) is logged and the walk continues, so
    // a dead primary CDN transparently hands over to the mirrors; the sha256/
    // size verification below applies to the payload whichever source served it.
    private static async Task DownloadItemAsync(MojangAssetManifestItem item, ILogger logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Url))
        {
            throw new InvalidOperationException("manifest entry has no source URL");
        }

        List<string> candidates = [item.Url, .. (item.FallbackUrls ?? []).Where(u => !string.IsNullOrWhiteSpace(u))];
        string destPath = GetAssetPath(item.File);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        string partPath = destPath + ".part";

        Exception? lastError = null;
        bool downloaded = false;
        foreach (string candidate in candidates)
        {
            try
            {
                using (var response = await HttpClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = new HttpRequestException($"HTTP {(int)response.StatusCode}");
                        logger.Information(
                            "Mojang asset source unavailable for {File}: {Url} answered {Status} — trying the next source",
                            item.File, candidate, (int)response.StatusCode);
                        continue;
                    }

                    await using (var content = await response.Content.ReadAsStreamAsync(cancellationToken))
                    await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await content.CopyToAsync(fileStream, cancellationToken);
                    }
                }

                downloaded = true;
                if (candidate == item.Url)
                {
                    logger.Debug("Mojang asset {File} downloaded from the primary source", item.File);
                }
                else
                {
                    logger.Warning(
                        "Mojang asset {File} restored from a mirror: {Url} (the primary source failed) — sha256 still verified",
                        item.File, candidate);
                }

                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                lastError = ex;
                logger.Information(
                    "Mojang asset source unreachable for {File}: {Url} ({Reason}) — trying the next source",
                    item.File, candidate, ex.Message);
            }
        }

        if (!downloaded)
        {
            throw lastError ?? new HttpRequestException($"no source answered 200 ({candidates.Count} tried)");
        }

        if (item.Size is { } expectedSize && new FileInfo(partPath).Length != expectedSize)
        {
            throw new InvalidDataException($"size mismatch (expected {expectedSize} bytes, got {new FileInfo(partPath).Length})");
        }

        if (item.Sha256 is { } expectedSha)
        {
            string actualSha = ComputeSha256(partPath) ?? throw new IOException("downloaded file could not be read back for hashing");
            if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"sha256 mismatch (expected {expectedSha}, got {actualSha})");
            }
        }

        File.Move(partPath, destPath, overwrite: true);
    }

    // ---------------------------------------------------------------------------
    // Panel helpers
    // ---------------------------------------------------------------------------

    /// <summary>Aggregate for the consent alert; null when the manifest is not loaded.</summary>
    public static MojangAssetGateSummary? GetSummary()
    {
        var manifest = _manifest;
        if (manifest is null)
        {
            return null;
        }

        var missing = GetMissingFiles();
        var unavailable = GetUnavailableFiles();
        var hosts = missing
            .Where(item => !string.IsNullOrWhiteSpace(item.Url) && Uri.TryCreate(item.Url, UriKind.Absolute, out _))
            .Select(item => new Uri(item.Url).Host)
            .Distinct()
            .ToList();

        return new MojangAssetGateSummary(
            manifest.Items.Count(item => item.Kind == "persona"),
            manifest.Items.Count(item => item.Kind == "thumbnail"),
            manifest.Items.Count(item => item.Kind == "skin-texture"),
            missing.Count,
            missing.Sum(item => item.Size ?? 0),
            unavailable.Count,
            hosts,
            MojangAssetsDir,
            manifest.GeneratedAt,
            HasConsent);
    }

    private static async Task RunPostProcessorAsync(ILogger logger)
    {
        try
        {
            await MojangAssetPostProcessor.RunAll(MojangAssetsDir);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Mojang asset post-processing failed (asset files are in place; every step must be idempotent)");
        }
    }
}
