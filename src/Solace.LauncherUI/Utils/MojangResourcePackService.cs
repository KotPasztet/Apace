using System.IO.Compression;
using Serilog;

namespace Solace.LauncherUI.Utils;

/// <summary>
/// Downloads and extracts the official Mojang 1.20.4 resource pack
/// for buildplate preview functionality.
/// </summary>
public static class MojangResourcePackService
{
    private const string ClientJarUrl = "https://piston-data.mojang.com/v1/objects/fd19469fed4a4b4c15b2d5133985f0e3e7816a8a/client.jar";
    private static readonly HttpClient _http = new();

    public static string ResourcePackDir => Path.GetFullPath(
        Path.Combine(Program.StaticDataDir, "resourcepacks", "java", "minecraft"));

    /// <summary>
    /// Checks if the resource pack is already installed.
    /// </summary>
    public static bool IsInstalled =>
        Directory.Exists(Path.Combine(ResourcePackDir, "blockstates")) &&
        Directory.Exists(Path.Combine(ResourcePackDir, "models")) &&
        Directory.Exists(Path.Combine(ResourcePackDir, "textures"));

    /// <summary>
    /// Downloads client.jar from Mojang, extracts assets/minecraft/,
    /// and places them in the resource pack directory.
    /// Progress is reported via callback (0.0 to 1.0).
    /// </summary>
    public static async Task<(bool Success, string? Error)> DownloadAndExtractAsync(
        Action<double>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"apace-resourcepack-{Guid.NewGuid():N}");
        var jarPath = Path.Combine(tmpDir, "client.jar");

        try
        {
            Directory.CreateDirectory(tmpDir);

            // Download client.jar
            Log.Information("Downloading Minecraft 1.20.4 client.jar from Mojang...");
            using var response = await _http.GetAsync(ClientJarUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var downloadedBytes = 0L;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = File.Create(jarPath);
            var buffer = new byte[8192];

            while (true)
            {
                var read = await contentStream.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloadedBytes += read;
                if (totalBytes > 0)
                    progressCallback?.Invoke((double)downloadedBytes / totalBytes);
            }

            progressCallback?.Invoke(1.0);
            Log.Information("Downloaded client.jar ({Size} bytes)", downloadedBytes);

            // Extract assets/minecraft/ from the JAR (which is a ZIP)
            var destDir = ResourcePackDir;
            Directory.CreateDirectory(destDir);

            var assetCount = 0;
            using (var zip = ZipFile.OpenRead(jarPath))
            {
                foreach (var entry in zip.Entries)
                {
                    // Only extract assets/minecraft/
                    if (!entry.FullName.StartsWith("assets/minecraft/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip directory entries
                    if (entry.FullName.EndsWith('/'))
                        continue;

                    var relativePath = entry.FullName["assets/minecraft/".Length..];
                    var destPath = Path.Combine(destDir, relativePath);
                    var destParent = Path.GetDirectoryName(destPath);
                    if (destParent is not null)
                        Directory.CreateDirectory(destParent);

                    entry.ExtractToFile(destPath, overwrite: true);
                    assetCount++;
                }
            }

            Log.Information("Extracted {Count} assets to {Dir}", assetCount, destDir);
            progressCallback?.Invoke(1.0);

            return (true, null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download/extract Mojang resource pack");
            return (false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* cleanup */ }
        }
    }
}
