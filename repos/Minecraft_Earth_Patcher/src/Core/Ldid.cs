using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Serilog;

namespace MCEPatcher.Core;

/// <summary>
/// ldid (Link Identity Editor) pseudo-signs Mach-O binaries with embedded entitlements.
/// The patched iOS app needs it because stripping the original App Store signature
/// (RemoveDrm) also strips its entitlements, and the Xbox sign-in library (XAL) stores its
/// tokens in the com.microsoft.xal / com.microsoft.adalcache keychain groups - iOS only
/// honors the keychain groups carried by the code signature, so XAL fails before it sends
/// any request when the entitlements are gone. Pseudo-signing the main executable and the
/// Xal framework binary embeds them again: sideload tools (Sideloadly, SideStore/AltStore)
/// read the entitlements embedded in the binary and re-apply them - re-prefixed with the
/// signing team's id, which a team wildcard profile covers - when they re-sign the app with
/// the user's Apple ID.
/// </summary>
public static class Ldid
{
    private const string Version = "v2.1.5-procursus8";

    private const string DownloadBaseUrl = $"https://github.com/ProcursusTeam/ldid/releases/download/{Version}/";

    // pinned binaries - sha256 verified after download (a tampered or corrupted tool
    // must never touch the patched binaries)
    private const string LinuxX64Sha256 = "d7d8c8b06ec913b2778e3e2e0306b5383e83fafe994a3f8acf25f4a525e2a9e9";
    private const string LinuxArm64Sha256 = "8878c8fb918b4047df94e703346913ba92d5901054095e3d5b7dfa192f9a624e";
    private const string MacosX64Sha256 = "d15baf446b7908b202210f3d852e8ba794c6fba4d7f83fe55f9db89b2a2ff9f6";
    private const string MacosArm64Sha256 = "99b8c3052c188184e259b9c512f8ceb7c40ca89e44c99bb96b38c4bd635ff4df";
    private const string WindowsX64Sha256 = "731054927436b05090e59ea58dd369eb11fcc4aab460c0ba1ae0c3a86b7ca475";

    public static readonly string FileName = OperatingSystem.IsWindows() ? "ldid.exe" : "ldid";

    // Absolute path of a distro-installed ldid (found once, preferred over the
    // runtime download); null when the pinned download is used instead.
    private static string? _systemPath;

    private static (string Asset, string Sha256)? Download =>
        OperatingSystem.IsWindows()
            ? RuntimeInformation.OSArchitecture is Architecture.X64
                ? ("ldid_w64_x86_64.exe", WindowsX64Sha256)
                : null
            : OperatingSystem.IsMacOS()
                ? RuntimeInformation.OSArchitecture is Architecture.Arm64
                    ? ("ldid_macosx_arm64", MacosArm64Sha256)
                    : ("ldid_macosx_x86_64", MacosX64Sha256)
                : OperatingSystem.IsLinux()
                    ? RuntimeInformation.OSArchitecture switch
                    {
                        Architecture.X64 => ("ldid_linux_x86_64", LinuxX64Sha256),
                        Architecture.Arm64 => ("ldid_linux_aarch64", LinuxArm64Sha256),
                        _ => null,
                    }
                    : null;

    /// <summary>
    /// Locates ldid for the running platform: a distro-installed binary first (the Apace
    /// Docker image ships the apt "ldid" package, which works offline and needs no
    /// runtime download), then the pinned runtime download as the fallback (same pattern
    /// as apktool / uber-apk-signer / patchelf, sha256 verified). Returns false when
    /// neither is available - the iOS patch job then fails loudly instead of producing
    /// an IPA without the XAL keychain entitlements.
    /// </summary>
    public static async Task<bool> EnsureAsync(CancellationToken cancellationToken = default)
    {
        string? system = FindSystemLdid();
        if (system is not null)
        {
            _systemPath = system;
            return true;
        }

        _systemPath = null;

        var download = Download;
        if (download is null)
        {
            Log.Warning($"No prebuilt ldid binary is available for {RuntimeInformation.OSDescription} - the patched binaries cannot carry embedded entitlements.");
            return false;
        }

        try
        {
            await DependencyDownloader.Download(DownloadBaseUrl + download.Value.Asset, FileName);
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not download ldid ({download.Value.Asset}): {ex.Message}");
            return false;
        }

        string path = Path.GetFullPath(FileName);
        byte[] bytes;

        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Warning($"Could not read the downloaded ldid binary: {ex.Message}");
            return false;
        }

        string hash = Convert.ToHexString(SHA256.HashData(bytes));

        if (!string.Equals(hash, download.Value.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning($"The downloaded ldid binary does not match the pinned sha256 (got {hash}, expected {download.Value.Sha256}) - refusing to use it and deleting it.");
            TryDelete(path);
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not mark ldid as executable: {ex.Message}");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Runs `ldid -S&lt;entitlements&gt; &lt;binary&gt;` - a pseudo-signature (no identity, like
    /// codesign -s -) whose only job is to carry the entitlements blob inside the binary.
    /// Returns false when ldid failed; the caller fails the job (an IPA whose binaries
    /// carry no entitlements is the "sign-in fails with no login page" bug).
    /// </summary>
    public static bool PseudoSign(string binaryPath, string entitlementsPath)
    {
        string tool = _systemPath ?? Path.GetFullPath(FileName);

        var process = U.Run(tool, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        [
            $"-S{entitlementsPath}",
            binaryPath,
        ]);

        process.WaitForExit();
        int exitCode = process.ExitCode;
        process.Close();
        process.Dispose();

        return exitCode is 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    /// Looks for a distro-installed ldid on the PATH (the apt package ships /usr/bin/ldid).
    /// Windows is skipped: no distro package exists there, so the pinned download is used.
    /// </summary>
    private static string? FindSystemLdid()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string dir in pathEnv.Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, "ldid");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // unreadable PATH entry - skip it
            }
        }

        return null;
    }
}
