using System.IO.Compression;

namespace Apace.LauncherUI;

internal static class GenoaResourcepackCache
{
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static string? _cachedPath;

    public static async Task<string?> GetCachePath()
    {
        if (_cachedPath is not null)
        {
            return _cachedPath;
        }

        await _lock.WaitAsync();
        try
        {
            if (_cachedPath is not null)
            {
                return _cachedPath;
            }

            var cachePath = Path.GetFullPath(Path.Combine(Program.StaticDataDir, "resourcepacks", "genoa_cache"));

            if (Directory.Exists(cachePath))
            {
                return _cachedPath = cachePath;
            }

            var resourcepackFilePath = Path.GetFullPath(Path.Combine(Program.StaticDataDir, "resourcepacks", "vanilla.zip"));

            if (!File.Exists(resourcepackFilePath))
            {
                return null;
            }

            using (var outerZip = await ZipFile.OpenReadAsync(resourcepackFilePath))
            {
                var innerZipEntry = outerZip.GetEntry("genoa.mcpack");

                if (innerZipEntry is null)
                {
                    return null;
                }

                using (var innerZip = new ZipArchive(await innerZipEntry.OpenAsync(), ZipArchiveMode.Read, false))
                {
                    await innerZip.ExtractToDirectoryAsync(cachePath);
                }
            }

            return _cachedPath = cachePath;
        }
        finally
        {
            _lock.Release();
        }
    }
}
