using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace MCEPatcher.Core;

public static class ApkProcessor
{
    public static readonly string ApkHash = "BED7864C4B1BCC2774332E6D5E1BFFBA40CAB6D27782D0C3FA272F27F613A79A";

    public static readonly string ResourcePackHash = "7473B7B99FD181453D7D520903726F62F3C2433FE941EA8968E6FE589EF5A9E7";

    internal static bool NonInteractive { get; private set; }

    public static async Task<bool> Run(Options options)
    {
        NonInteractive = options.NonInteractive;
        HashSet<string> patches = new(options.Patches);

        FileInfo inApk = new FileInfo(options.InApk);
        FileInfo outApk = new FileInfo(options.OutApk);
        DirectoryInfo decodedDir = new DirectoryInfo(options.DecodedDir);

        if (!inApk.Exists)
        {
            Log.Fatal($"In apk '{inApk.FullName}' doesn't exist");
            return false;
        }

        Dictionary<string, string> variables = new();

        foreach (var item in options.Variables)
        {
            string[] split = item.Split('=');

            if (split.Length < 2)
            {
                Log.Fatal($"Variable '{item}' doesn't have value (no '=')");
                return false;
            }
            else if (split.Length > 2)
            {
                Log.Fatal($"Too many '=' in variable ({item})");
                return false;
            }

            var (name, value) = (split[0], split[1]);

            if (string.IsNullOrWhiteSpace(name))
            {
                Log.Fatal($"Name of a variable can't be empty");
                return false;
            }
            else if (variables.ContainsKey(name))
            {
                Log.Fatal($"Variable '{name}' is defined multiple times");
                return false;
            }

            variables.Add(name, value);
        }

        if (!options.SkipDecode || !options.SkipBuild)
        {
            await DependencyDownloader.Download("https://github.com/iBotPeaches/Apktool/releases/download/v3.0.3/apktool_3.0.3.jar", APK.FileName);

            // the Google build-tools zip (x86-64 only) is needed just for
            // zipalign; when ZIPALIGN_PATH provides a native binary the
            // download is skipped entirely (see SystemTools / BuildTools)
            if (SystemTools.ZipAlignPath is null)
            {
                await DependencyDownloader.Download(BuildTools.DownloadUrl, BuildTools.FileName);
            }
        }

        if (options.AndroidOSVersion >= 15)
        {
            await DependencyDownloader.Download(PatchElf.DownloadUrl, PatchElf.FileName);
        }

        await DependencyDownloader.Download(ArCoreUpdater.DownloadUrl, ArCoreUpdater.FileName);

        if (!options.SkipSign)
        {
            await DependencyDownloader.Download("https://github.com/patrickfav/uber-apk-signer/releases/download/v1.3.0/uber-apk-signer-1.3.0.jar", Signer.FileName);
        }

        if (options.SkipDecode)
        {
            Log.Information("Decoding skipped");
        }
        else
        {
            Log.Information("*****Decoding apk*****");
            if (!APK.Decode(inApk, decodedDir))
            {
                return false;
            }

            if (!File.Exists(Path.Combine(decodedDir.FullName, "lib", "arm64-v8a", "libgenoa.so")))
            {
                Log.Error("libgenoa.so does not exist, wrong apk?");
                return false;
            }

            Log.Debug("Done");
        }

        // does not get properly extended if not done before the hexdump and will most likely be done anyway, TODO: remove once the TODO in Patcher.PatchFile is fixed
        Log.Information($"Applying patch '{ExtendLibgenoa.Name}'");
        ExtendLibgenoa.Extent(decodedDir);
        patches.Remove(ExtendLibgenoa.Name);
        Log.Debug("Done");

        Patcher patcher = new Patcher("Patches", decodedDir.FullName);
        patcher.Patch(patches, variables, patches.Contains(ExtendLibgenoa.Name) ? new()
        {
            [ExtendLibgenoa.Name] = PatchInfo.Default
        } : new());

        if (!string.IsNullOrEmpty(options.ResourcePack))
        {
            if (!await ResourcePackPatcher.Patch(options))
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(options.IconPath))
        {
            Log.Information("Replacing app icon");
            IconPatcher.PatchAndroidIcon(decodedDir.FullName, options.IconPath);
            Log.Debug("Done");
        }

        await ArCoreUpdater.UpdateArCoreLibs(decodedDir);

        if (options.AndroidOSVersion >= 15)
        {
            await PatchElf.PatchPageSizeAsync(decodedDir, Log.Logger);
        }

        if (options.SkipBuild)
        {
            Log.Information($"Building skipped");
        }
        else
        {
            Log.Information("*****Building apk*****");
            if (!APK.Encode(decodedDir, outApk))
            {
                return false;
            }

            Log.Debug("Done");

            Log.Information("*****Aligning apk*****");
            if (!await BuildTools.AlignAsync(outApk))
            {
                return false;
            }

            Log.Debug("Done");
        }

        if (options.SkipSign)
        {
            Log.Information($"Signing skipped");
        }
        else
        {
            Log.Information("*****Signing apk*****");
            if (!Signer.Sign(outApk, new DirectoryInfo("Signed")))
            {
                return false;
            }

            Log.Debug("Done");
        }

        Log.Information("Finished");

        return true;
    }

    public static bool VerifyApkHash(Stream stream)
    {
        byte[] hashBytes = SHA256.HashData(stream);

        var hash = Convert.ToHexString(hashBytes);

        return hash == ApkHash;
    }

    public static bool VerifyResourcePackHash(Stream stream)
    {
        byte[] hashBytes = SHA256.HashData(stream);

        var hash = Convert.ToHexString(hashBytes);

        return hash == ResourcePackHash;
    }

    public sealed class Options
    {
        public required string InApk { get; init; }

        public required string OutApk { get; init; }

        public required string? ResourcePack { get; init; }

        /// <summary>Optional png/webp image to use as the app icon.</summary>
        public string? IconPath { get; init; }

        public required string DecodedDir { get; init; }

        public required int AndroidOSVersion { get; init; }

        public required IEnumerable<string> Patches { get; init; }

        public required IEnumerable<string> Variables { get; init; }

        public bool NonInteractive { get; init; }

        public bool SkipDecode { get; init; }

        public bool SkipBuild { get; init; }

        public bool SkipSign { get; init; }
    }
}
