using System.IO.Compression;
using Serilog;

namespace MCEPatcher.Core;

internal static class ArCoreUpdater
{
    public static string FileName = $"arcore-{Version}.zip";

    public static string DownloadUrl = $"https://dl.google.com/android/maven2/com/google/ar/core/{Version}/core-{Version}.aar";

    private const string Version = "1.50.0";

    public static async Task UpdateArCoreLibs(DirectoryInfo decodedDir, CancellationToken cancellationToken = default)
    {
        Log.Information($"Updating ARCore libs to {Version}");

        await using var zip = await ZipFile.OpenReadAsync(FileName, cancellationToken);

        var sdkC = zip.GetEntry("jni/arm64-v8a/libarcore_sdk_c.so")!;
        await sdkC.ExtractToFileAsync(Path.Combine(decodedDir.FullName, "lib", "arm64-v8a", "libarcore_sdk_c.so"), overwrite: true);

        var sdkJni = zip.GetEntry("jni/arm64-v8a/libarcore_sdk_jni.so")!;
        await sdkJni.ExtractToFileAsync(Path.Combine(decodedDir.FullName, "lib", "arm64-v8a", "libarcore_sdk_jni.so"), overwrite: true);

        Log.Debug("Done");
    }
}
