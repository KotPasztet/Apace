using Serilog;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ILogger = Serilog.ILogger;

namespace Apace.LauncherUI.Services;

// Fetches the nine default character textures (skins/Steve.png, skins/Alex.png,
// skins/Sunny.png, skins/Kai.png, skins/Makena.png, skins/Zuri.png, skins/Efe.png,
// skins/Ari.png, skins/Noor.png) into <mojang-assets>/skins/.
//
// Nothing downloaded these textures since the Mojang asset gate was removed (the
// gate used to own their download; its sources for everything else went dark and
// the whole consent flow was deleted). MojangSkinPackBuilder — the only consumer —
// bails on an empty skins/ dir, the "Minecraft Default Skins" pack zip never lands
// and the dressing room shows the offer with two empty rows (the client's
// skinbinary request 404s).
//
// The facts live in the committed mojang-assets.manifest.json (kind "skin-texture":
// minecraft.wiki source URLs, sha256, size — all verified at manifest generation
// time). This step downloads the entries that name one of the pack builder's skins
// (MojangSkinPackBuilder.DefaultSkinNames) into the assets dir, verifying sha256
// and size before an atomic move into place; files already present with matching
// size/hash are skipped, so re-running on a complete volume is a no-op (the
// MojangAssetPostProcessor contract).
//
// Contract (see MojangAssetPostProcessor): best-effort and idempotent — a failed
// entry logs a warning and the step continues; it never throws and never blocks
// the server start. Registered BEFORE MojangSkinPackBuilder so the textures are
// in place when the pack build runs in the same pipeline pass.
public static class MojangSkinTextureFetcher
{
    private const string ManifestFileName = "mojang-assets.manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HttpClient HttpClient = CreateHttpClient();

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
        _ = client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"ApaceProject/Apace/{Assembly.GetExecutingAssembly().GetName().Version}");
        return client;
    }

    /// <summary>The post-processor step delegate (registered in MojangAssetPostProcessor).</summary>
    public static async Task FetchAsync(string mojangAssetsDir)
    {
        try
        {
            await FetchAllAsync(mojangAssetsDir, Log.Logger);
        }
        catch (Exception exception)
        {
            // Post-processor contract: never throw — startup logs failures but
            // must not be blocked by a derived-asset step.
            Log.Logger.Warning(exception, "Default skin texture fetch failed (non-fatal)");
        }
    }

    private static async Task FetchAllAsync(string mojangAssetsDir, ILogger logger)
    {
        var manifest = TryLoadManifest(mojangAssetsDir, logger);
        if (manifest is null)
        {
            logger.Warning(
                "Default skin texture fetch: no mojang asset manifest — the Minecraft Default Skins pack cannot be built until the textures are present under {SkinsDir}",
                Path.Combine(mojangAssetsDir, "skins"));
            return;
        }

        // Only verified skin-texture entries whose target is one of the pack
        // builder's skins/<Name>.png files (an install that drops a foreign or
        // unverified entry into the manifest must not gain a download).
        var targets = new List<(ManifestItem Item, string DestPath)>();
        foreach (var item in manifest.Items)
        {
            if (item.Kind != "skin-texture")
            {
                continue;
            }

            if (!item.Verified)
            {
                logger.Warning("Default skin texture {File}: manifest entry has no verified source — skipping", item.File);
                continue;
            }

            if (!IsManifestFileSafe(item.File))
            {
                logger.Warning("Default skin texture {File}: unsafe manifest path — skipping", item.File);
                continue;
            }

            string normalized = item.File.Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(normalized);
            if (!MojangSkinPackBuilder.DefaultSkinNames.Contains(name)
                || !string.Equals(normalized, $"skins/{name}.png", StringComparison.OrdinalIgnoreCase))
            {
                logger.Warning("Default skin texture {File}: not one of the Minecraft Default Skins pack builder's skins — skipping", item.File);
                continue;
            }

            targets.Add((item, Path.Combine(mojangAssetsDir, normalized)));
        }

        int fetched = 0;
        int failed = 0;
        var failedFiles = new List<string>();
        foreach (var (item, destPath) in targets)
        {
            if (IsTexturePresent(destPath, item))
            {
                continue;
            }

            // Transient network failures (dead host, reset connection, truncated
            // body) retry a few times before the texture is reported as failed.
            const int MaxAttempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadItemAsync(item, destPath, logger);
                    fetched++;
                    break;
                }
                catch (Exception ex)
                {
                    if (attempt >= MaxAttempts)
                    {
                        failed++;
                        failedFiles.Add(item.File);
                        logger.Warning("Default skin texture fetch failed for {File}: {Reason}", item.File, ex.Message);
                        break;
                    }

                    logger.Warning("Default skin texture fetch attempt {Attempt}/{Max} failed for {File}: {Reason}", attempt, MaxAttempts, item.File, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }
        }

        int skipped = targets.Count - fetched - failed;
        logger.Information(
            "Default skin textures: fetched {Fetched}, skipped {Skipped} (already present), failed {Failed} of {Total}",
            fetched, skipped, failed, targets.Count);
        if (failed > 0)
        {
            logger.Warning(
                "Default skin texture fetch finished with {Failed} failure(s): {Files} — the Minecraft Default Skins pack cannot be built until they succeed (MojangSkinPackBuilder bails on an incomplete skins/ dir and the dressing room rows stay empty)",
                failed, string.Join(", ", failedFiles));
        }
    }

    // ---------------------------------------------------------------------------
    // Manifest loading (the same resolver the Mojang asset gate used)
    // ---------------------------------------------------------------------------

    private static AssetManifest? TryLoadManifest(string mojangAssetsDir, ILogger logger)
    {
        foreach (string candidate in ManifestPathCandidates(mojangAssetsDir))
        {
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                var manifest = JsonSerializer.Deserialize<AssetManifest>(File.ReadAllText(fullPath), JsonOptions);
                if (manifest?.Items is null || manifest.Items.Count == 0)
                {
                    logger.Warning("Mojang asset manifest at {Path} has no items — ignoring", fullPath);
                    continue;
                }

                logger.Information("Mojang asset manifest loaded from {Path} ({Count} items)", fullPath, manifest.Items.Count);
                return manifest;
            }
            catch (Exception ex)
            {
                logger.Warning("Could not load Mojang asset manifest candidate {Path}: {Reason}", Path.GetFullPath(candidate), ex.Message);
            }
        }

        logger.Warning("Mojang asset manifest not found — searched: {Candidates}", string.Join(", ", ManifestPathCandidates(mojangAssetsDir).Select(Path.GetFullPath)));
        return null;
    }

    private static IEnumerable<string> ManifestPathCandidates(string mojangAssetsDir)
    {
        // Inside the assets dir itself (operator-placed override, next to the
        // data the manifest describes).
        yield return Path.Combine(mojangAssetsDir, ManifestFileName);

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

    // ---------------------------------------------------------------------------
    // Download (the same source walk / verification idioms the Mojang asset gate used)
    // ---------------------------------------------------------------------------

    /// <summary>True when the texture exists and matches its recorded size AND sha256 (when known).</summary>
    private static bool IsTexturePresent(string destPath, ManifestItem item)
    {
        var info = new FileInfo(destPath);
        if (!info.Exists || info.Length == 0)
        {
            return false;
        }

        if (item.Size is { } expectedSize && info.Length != expectedSize)
        {
            return false;
        }

        if (item.Sha256 is { } expectedSha)
        {
            return string.Equals(ComputeSha256(destPath), expectedSha, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static async Task DownloadItemAsync(ManifestItem item, string destPath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(item.Url))
        {
            throw new InvalidOperationException("manifest entry has no source URL");
        }

        List<string> candidates = [item.Url, .. (item.FallbackUrls ?? []).Where(u => !string.IsNullOrWhiteSpace(u))];
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        string partPath = destPath + ".part";

        Exception? lastError = null;
        bool downloaded = false;
        foreach (string candidate in candidates)
        {
            try
            {
                using (var response = await HttpClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = new HttpRequestException($"HTTP {(int)response.StatusCode}");
                        logger.Information(
                            "Default skin texture source unavailable for {File}: {Url} answered {Status} — trying the next source",
                            item.File, candidate, (int)response.StatusCode);
                        continue;
                    }

                    await using (var content = await response.Content.ReadAsStreamAsync())
                    await using (var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        await content.CopyToAsync(fileStream);
                    }
                }

                downloaded = true;
                if (candidate != item.Url)
                {
                    logger.Warning(
                        "Default skin texture {File} restored from a mirror: {Url} (the primary source failed) — sha256 still verified",
                        item.File, candidate);
                }

                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                lastError = ex;
                logger.Information(
                    "Default skin texture source unreachable for {File}: {Url} ({Reason}) — trying the next source",
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

    /// <summary>One entry of <c>mojang-assets.manifest.json</c> (the fields the fetcher needs).</summary>
    private sealed record ManifestItem
    {
        /// <summary>"persona" | "thumbnail" | "skin-texture".</summary>
        public string Kind { get; init; } = "";

        /// <summary>Relative target path inside the Mojang assets dir, e.g. "skins/Steve.png".</summary>
        public string File { get; init; } = "";

        /// <summary>Source URL the file is downloaded from (a recorded fact, may be offline).</summary>
        public string Url { get; init; } = "";

        /// <summary>Mirror locations tried in order when the primary URL fails.</summary>
        public List<string>? FallbackUrls { get; init; }

        /// <summary>Known sha256 of the file (hex), when it could be verified at manifest generation time.</summary>
        public string? Sha256 { get; init; }

        public long? Size { get; init; }

        /// <summary>True when the URL was verified reachable AND the payload hash confirmed when the manifest was generated.</summary>
        public bool Verified { get; init; }

        /// <summary>Optional display name (the default skin textures carry the skin name).</summary>
        public string? Name { get; init; }
    }

    private sealed record AssetManifest
    {
        public List<ManifestItem> Items { get; init; } = [];
    }
}
