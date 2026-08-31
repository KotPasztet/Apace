using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Serilog;

namespace MCEPatcher.Core;

public static class PatchElf
{
    public static string DownloadUrl => $"https://github.com/NixOS/patchelf/releases/download/{Version}/" + DownloadFileName;

    public static readonly string FileName = OperatingSystem.IsWindows() ? $"patchelf-{Version}.exe" : $"patchelf-{Version}.tar.gz";

    private const string Version = "0.19.1";

    private static readonly string ExtractedDirectory = $"patchelf-{Version}";

    private static readonly string PatchElfPath = OperatingSystem.IsWindows()
        ? FileName
        : Path.Combine(ExtractedDirectory, "bin", "patchelf");

    private static string DownloadFileName
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return RuntimeInformation.OSArchitecture switch
                {
                    Architecture.X64 => $"patchelf-win64-{Version}.exe",
                    Architecture.X86 => $"patchelf-win32-{Version}.exe",
                    _ => throw new PlatformNotSupportedException($"Windows architecture '{RuntimeInformation.OSArchitecture}' is not supported.")
                };
            }

            if (OperatingSystem.IsLinux())
            {
                return RuntimeInformation.OSArchitecture switch
                {
                    Architecture.X64 => $"patchelf-{Version}-x86_64.tar.gz",
                    Architecture.X86 => $"patchelf-{Version}-i686.tar.gz",
                    Architecture.Arm64 => $"patchelf-{Version}-aarch64.tar.gz",
                    Architecture.Arm => $"patchelf-{Version}-armv7l.tar.gz",
                    Architecture.Ppc64le => $"patchelf-{Version}-ppc64le.tar.gz",
                    Architecture.RiscV64 => $"patchelf-{Version}-riscv64.tar.gz",
                    Architecture.S390x => $"patchelf-{Version}-s390x.tar.gz",
                    _ => throw new PlatformNotSupportedException($"Linux architecture '{RuntimeInformation.OSArchitecture}' is not supported.")
                };
            }

            throw new PlatformNotSupportedException("PatchElf pre-built binaries are only available for Windows and Linux.");
        }
    }

    public static async Task<bool> PatchPageSizeAsync(DirectoryInfo decodedDir, ILogger logger, CancellationToken cancellationToken = default)
    {
        await EnsureExtractedAsync(cancellationToken);

        var libsDir = Path.Combine(decodedDir.FullName, "lib", "arm64-v8a");

        foreach (var fileName in (IEnumerable<string>)["libazurespatialanchorsndk.so", "libc++_shared.so", "libfmod.so", "libfmodstudio.so", "libgenoa.so"])
        {
            if (!await PatchPageSizeFileAsync(Path.Combine(libsDir, fileName), logger, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> PatchPageSizeFileAsync(string file, ILogger logger, CancellationToken cancellationToken = default)
    {
        logger.Information($"Patching page size for {file}");

        var process = U.Run(PatchElfPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        [
            "--page-size", "16384",
            file,
        ]);

        process.WaitForExit();
        int exitCode = process.ExitCode;
        process.Close();
        process.Dispose();

        if (exitCode is not 0)
        {
            return false;
        }

        logger.Debug("Done");

        return true;
    }

    private static async Task EnsureExtractedAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (!File.Exists(PatchElfPath))
        {
            await using FileStream fs = File.OpenRead(FileName);
            await using GZipStream decompressionStream = new GZipStream(fs, CompressionMode.Decompress);

            if (Directory.Exists(ExtractedDirectory))
            {
                Directory.Delete(ExtractedDirectory, true);
            }

            Directory.CreateDirectory(ExtractedDirectory);

            TarFile.ExtractToDirectory(decompressionStream, ExtractedDirectory, overwriteFiles: true);
        }

        File.SetUnixFileMode(
            PatchElfPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
        );
    }
}
