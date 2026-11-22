using Serilog;
using System.Security.Cryptography;
using System.Text.Json;
using ILogger = Serilog.ILogger;

namespace Apace.ApiServer.Utils;

// Startup validation of the additional Mojang assets for the ApiServer's
// direct (panel-less) start path.
//
// The panel's asset gate downloads the assets described by the committed
// manifest (mojang-assets.manifest.json) into the persistent data dir; the
// panel passes the dir with --mojang-assets-dir and the gate requirement with
// --require-mojang-assets. This helper re-verifies the same completeness
// rules: every VERIFIED manifest entry must be present (sha256-checked when
// the manifest records one); entries with no reachable source at manifest
// generation time ("verified": false) are never required.
public static class MojangAssets
{
    public const string ManifestFileName = "mojang-assets.manifest.json";

    private static readonly string[] ManifestPathCandidates =
    [
        // inside the assets dir itself (operator-placed override)
        "./" + ManifestFileName,
        // deployment layout: staticdata next to the components dir
        "../staticdata/playfab/" + ManifestFileName,
        // dev checkouts (working directory is the project's bin output —
        // bin/Debug/net10.0 is five levels below the repo root)
        "../staticdata-extras/playfab/" + ManifestFileName,
        "../../staticdata-extras/playfab/" + ManifestFileName,
        "../../../staticdata-extras/playfab/" + ManifestFileName,
        "../../../../staticdata-extras/playfab/" + ManifestFileName,
        "../../../../../staticdata-extras/playfab/" + ManifestFileName,
    ];

    /// <summary>Resolves the manifest path (or null); logs which candidate was used.</summary>
    public static string? ResolveManifestPath(string assetsDir, ILogger logger)
    {
        var candidates = new List<string>(ManifestPathCandidates)
        {
            Path.Combine(assetsDir, ManifestFileName),
        };

        foreach (string candidate in candidates)
        {
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch (Exception ex)
            {
                logger.Debug("Skipping Mojang asset manifest candidate {Path}: {Reason}", candidate, ex.Message);
            }
        }

        logger.Warning("Mojang asset manifest not found — searched: {Candidates}", string.Join(", ", candidates));
        return null;
    }

    /// <summary>
    /// Fallback signal for the no-manifest case: the legacy staticdata layout
    /// still has persona piece binaries (pre-gate installs / dev checkouts with
    /// staticdata-extras committed), so the CDN controllers can serve them.
    /// Mirrors the panel gate's missing-manifest policy (docs/mojang-asset-gate.md).
    /// </summary>
    public static bool HasLegacyPersonaAssets(string staticDataPath, string assetsDir, ILogger logger)
    {
        var candidates = new List<string>();
        foreach (string manifestCandidate in ManifestPathCandidates)
        {
            string? directory = Path.GetDirectoryName(manifestCandidate);
            if (directory is not null)
            {
                candidates.Add(Path.Combine(directory, "persona"));
            }
        }

        candidates.Add(Path.Combine(staticDataPath, "playfab", "persona"));
        candidates.Add(Path.Combine(assetsDir, "playfab", "persona"));

        foreach (string candidate in candidates.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                if (Directory.EnumerateFiles(candidate, "*.zip").Any())
                {
                    logger.Information("Legacy persona assets found in {Dir} — proceeding without the manifest (completeness cannot be verified)", candidate);
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

    /// <summary>
    /// Counts the VERIFIED manifest entries missing from <paramref name="assetsDir"/>
    /// (sha256-checked when recorded). Returns null when the manifest cannot be read.
    /// </summary>
    public static int? CountMissing(string assetsDir, string manifestPath, ILogger logger)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                logger.Warning("Mojang asset manifest at {Path} has no items array", manifestPath);
                return null;
            }

            int missing = 0;
            foreach (var item in items.EnumerateArray())
            {
                bool verified = item.TryGetProperty("verified", out var verifiedProp) && verifiedProp.ValueKind == JsonValueKind.True;
                if (!verified || !item.TryGetProperty("file", out var fileProp) || fileProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string file = fileProp.GetString() ?? "";
                if (file.Contains("..") || Path.IsPathRooted(file))
                {
                    continue;
                }

                string path = Path.Combine(assetsDir, file.Replace('\\', '/'));
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0)
                {
                    missing++;
                    continue;
                }

                if (item.TryGetProperty("sha256", out var shaProp) && shaProp.ValueKind == JsonValueKind.String)
                {
                    string expected = shaProp.GetString() ?? "";
                    string? actual = ComputeSha256(path);
                    if (actual is null || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        missing++;
                    }
                }
            }

            return missing;
        }
        catch (Exception ex)
        {
            logger.Warning("Could not read the Mojang asset manifest at {Path}: {Reason}", manifestPath, ex.Message);
            return null;
        }
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
}
