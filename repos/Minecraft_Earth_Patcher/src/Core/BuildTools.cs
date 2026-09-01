using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Serilog;

namespace MCEPatcher.Core;

internal static class BuildTools
{
    public static string DownloadUrl => OperatingSystem.IsWindows()
        ? "https://dl.google.com/android/repository/build-tools_r35.0.1_windows.zip"
        : OperatingSystem.IsLinux()
        ? "https://dl.google.com/android/repository/build-tools_r35.0.1_linux.zip"
        : OperatingSystem.IsMacOS()
        ? "https://dl.google.com/android/repository/build-tools_r35.0.1_macosx.zip"
        : throw new InvalidOperationException($"OS ({RuntimeInformation.OSDescription}) is not supported.");

    public static readonly string FileName = "build-tools-35.zip";

    private static readonly string ExtractedDirectory = "build-tools-35";

    private static string ZipAlignName => OperatingSystem.IsWindows()
        ? "zipalign.exe"
        : OperatingSystem.IsLinux()
        ? "zipalign"
        : OperatingSystem.IsMacOS()
        ? "zipalign"
        : throw new InvalidOperationException($"OS ({RuntimeInformation.OSDescription}) is not supported.");

    private static readonly string ZipAlignPath = Path.Combine(ExtractedDirectory, "android-15", ZipAlignName);

    public static async Task<bool> AlignAsync(FileInfo apkFile, CancellationToken cancellationToken = default)
    {
        // Google's build-tools ship x86-64 binaries only; ZIPALIGN_PATH can
        // override them with a native binary (e.g. Debian's zipalign on ARM64,
        // see SystemTools).
        string zipAlign;

        if (SystemTools.ZipAlignPath is { } systemZipAlign)
        {
            zipAlign = systemZipAlign;
        }
        else
        {
            await EnsureExtractedAsync(cancellationToken);
            zipAlign = ZipAlignPath;
        }

        var alignedApkName = apkFile.FullName + ".aligned";

        // Preferred: -P 16 aligns uncompressed .so files to 16 KiB pages, as
        // required by 16 KiB-page devices (Android 15+) that load libraries
        // straight from the APK. Older zipalign builds (e.g. Debian's, based
        // on AOSP 10) only know the deprecated -p (4 KiB page-align for .so)
        // and abort with "ERROR: unknown flag -P" before creating any output
        // file, so retrying with -p is safe. The -p fallback still yields a
        // valid, 4-byte aligned APK; it only loses the 16 KiB zip-level .so
        // alignment (irrelevant when the app extracts native libs at install
        // time).
        string[] preferredArgs = ["-f", "-P", "16", "4", apkFile.FullName, alignedApkName];
        string[] fallbackArgs = ["-f", "-p", "4", apkFile.FullName, alignedApkName];

        foreach (var (args, isFallback) in new[] { (preferredArgs, false), (fallbackArgs, true) })
        {
            if (isFallback)
            {
                Log.Warning("zipalign does not support -P <pagesize_kb>; retrying with -p (4 KiB page alignment for .so files).");
                File.Delete(alignedApkName);
            }

            var process = U.Run(zipAlign, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), args);

            process.WaitForExit();
            int exitCode = process.ExitCode;
            process.Close();
            process.Dispose();

            if (exitCode is 0)
            {
                File.Move(alignedApkName, apkFile.FullName, overwrite: true);
                File.Delete(alignedApkName);

                return true;
            }
        }

        return false;
    }

    private static async Task EnsureExtractedAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ZipAlignPath))
        {
            await ZipFile.ExtractToDirectoryAsync(FileName, ExtractedDirectory, overwriteFiles: true, cancellationToken);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                ZipAlignPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            );
        }
    }
}
