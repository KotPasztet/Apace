using System.Diagnostics;
using System.Net;
using MCEPatcher.Core;
using Serilog;

namespace Solace.LauncherUI.Patcher;

/// <summary>
/// Runs Minecraft Earth client patch jobs (see repos/Minecraft_Earth_Patcher,
/// integrated as the MCEPatcher.Core library).
///
/// Jobs are serialized (one at a time) because MCEPatcher.Core resolves its
/// patch files, downloaded build tools and the signing output directory
/// relative to the process working directory. During a job the CWD is
/// switched to a shared work directory (which caches the downloaded
/// dependencies across jobs); everything job-specific (input, decoded tree,
/// output) lives inside the job's own directory.
/// </summary>
public sealed class PatcherService
{
    private const int MaxKeptJobs = 10;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object jobsLock = new();
    private readonly List<PatchJob> jobs = [];

    public static string BaseDir => Path.Combine(Program.DataDir, "patcher");

    public static string WorkDir => Path.Combine(BaseDir, "work");

    /// <summary>Default Apace icon shipped with the panel (images/Apace_Favicon.png).</summary>
    public static string DefaultIconPath => Path.Combine(AppContext.BaseDirectory, "images", "Apace_Favicon.png");

    public IReadOnlyList<PatchJob> Jobs
    {
        get
        {
            lock (jobsLock)
            {
                return jobs.ToArray();
            }
        }
    }

    public PatchJob? GetJob(string id)
    {
        lock (jobsLock)
        {
            return jobs.FirstOrDefault(job => job.Id == id);
        }
    }

    /// <summary>
    /// Validates the request, queues the job and returns it immediately.
    /// Actual patching happens in the background; follow <see cref="PatchJob.Status"/>.
    /// </summary>
    public PatchJob CreateJob(PatchRequest request)
    {
        Validate(request);

        var job = new PatchJob
        {
            Mode = request.Mode,
            Platform = request.Platform,
            WorkDir = Path.Combine(BaseDir, "jobs", $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}"),
            InputPath = request.InputPath,
            InputFileName = request.InputFileName,
        };

        Directory.CreateDirectory(job.WorkDir);

        lock (jobsLock)
        {
            jobs.Insert(0, job);

            while (jobs.Count > MaxKeptJobs)
            {
                var oldest = jobs.Last(job => job.IsFinished);

                if (oldest is null)
                {
                    break;
                }

                jobs.Remove(oldest);

                try
                {
                    Directory.Delete(oldest.WorkDir, true);
                }
                catch
                {
                }
            }
        }

        _ = RunJobAsync(job, request);

        return job;
    }

    private async Task RunJobAsync(PatchJob job, PatchRequest request)
    {
        await gate.WaitAsync();

        var originalDir = Environment.CurrentDirectory;

        job.MarkRunning();

        try
        {
            EnsureWorkDir();
            Environment.CurrentDirectory = WorkDir;
            PatchLogSink.CurrentJob = job;

            job.AddLog($"{DateTimeOffset.Now:HH:mm:ss} [INF] Starting {job.Platform} patch ({job.Mode} mode)");

            bool success = job.Platform == PatchPlatform.Android
                ? await RunAndroidAsync(job, request)
                : await RunIosAsync(job, request);

            if (!success)
            {
                job.MarkFailed("Patching failed - see the log for details.");
                return;
            }

            var outputPath = Path.Combine(job.WorkDir, BuildOutputFileName(request));
            job.MarkSucceeded(outputPath);

            job.AddLog($"{DateTimeOffset.Now:HH:mm:ss} [INF] Done: {Path.GetFileName(outputPath)}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Patch job {JobId} failed", job.Id);
            job.AddLog($"{DateTimeOffset.Now:HH:mm:ss} [ERR] {ex.Message}");
            job.MarkFailed(ex.Message);
        }
        finally
        {
            PatchLogSink.CurrentJob = null;
            Environment.CurrentDirectory = originalDir;
            gate.Release();
            CleanupJobWorkingFiles(job);
        }
    }

    private static async Task<bool> RunAndroidAsync(PatchJob job, PatchRequest request)
    {
        var a = request.Android ?? throw new ArgumentException("Android options are required for an Android patch.");

        if (!IsJavaAvailable())
        {
            throw new InvalidOperationException(
                "Java (java in PATH) is required to patch APKs (apktool and the signer are Java tools), but it was not found on this machine.");
        }

        if (SystemTools.Aapt2Path is { } aapt2)
        {
            job.AddLog($"{DateTimeOffset.Now:HH:mm:ss} [INF] Using system aapt2 ({aapt2}) instead of the one bundled with apktool.");
        }

        if (SystemTools.ZipAlignPath is { } zipAlign)
        {
            job.AddLog($"{DateTimeOffset.Now:HH:mm:ss} [INF] Using system zipalign ({zipAlign}) instead of the Google build-tools one.");
        }

        using (var fs = File.OpenRead(job.InputPath))
        {
            if (!ApkProcessor.VerifyApkHash(fs))
            {
                job.AddLog($"{DateTimeOffset.Now:HH:mm:ss} [WRN] The .apk file hash does not match the supported original - patching may fail.");
            }
        }

        if (request.ResourcePackPath is not null)
        {
            using (var fs = File.OpenRead(request.ResourcePackPath))
            {
                if (!ApkProcessor.VerifyResourcePackHash(fs))
                {
                    job.AddLog($"{DateTimeOffset.Now:HH:mm:ss} [WRN] The resource pack hash does not match - the game may not function correctly.");
                }
            }
        }

        var options = new ApkProcessor.Options
        {
            NonInteractive = true,
            InApk = job.InputPath,
            OutApk = Path.Combine(job.WorkDir, BuildOutputFileName(request)),
            ResourcePack = request.ResourcePackPath,
            IconPath = a.ChangeIcon ? ResolveIconPath(job) : null,
            DecodedDir = Path.Combine(job.WorkDir, "decoded"),
            AndroidOSVersion = a.AndroidVersion,
            Patches = GetAndroidPatches(a),
            Variables = GetAndroidVariables(a),
        };

        return await ApkProcessor.Run(options);
    }

    private static Task<bool> RunIosAsync(PatchJob job, PatchRequest request)
    {
        var a = request.Ios ?? throw new ArgumentException("iOS options are required for an iOS patch.");

        using (var fs = File.OpenRead(job.InputPath))
        {
            if (!IpaProcessor.VerifyHash(fs))
            {
                job.AddLog($"{DateTimeOffset.Now:HH:mm:ss} [WRN] The .ipa file hash does not match the supported original - patching may fail.");
            }
        }

        var iconPath = a.ChangeIcon ? ResolveIconPath(job) : null;

        var options = new IpaProcessor.Options
        {
            Autonomous = true,
            InIpa = job.InputPath,
            OutIpa = Path.Combine(job.WorkDir, BuildOutputFileName(request)),
            DecodedDir = Path.Combine(job.WorkDir, "decoded"),
            Protocol = (int)a.LoginServerProtocol,
            Hostname = a.LoginServerHostname,
            LocatorProtocol = (int)a.LocatorProtocol,
            LocatorHostname = a.LocatorHostname,
            AppName = a.AppName,
            ChangeIcon = iconPath is not null,
            IconPath = iconPath ?? "",
            ChangeLocatorAddress = a.ChangeLocatorAddress,
            ChangeXalAuthAddress = a.ChangeLoginServerAddress && a.ChangeXalAuthAddress,
            ForceInAppWebView = a.ChangeLoginServerAddress && a.ForceInAppWebView,
            ForceInteractiveSignIn = a.ForceInteractiveSignIn,
            ChangePlayfabApiAddress = a.ChangeLoginServerAddress && a.ChangePlayfabApiAddress,
            ChangeXboxABAddress = a.ChangeLoginServerAddress && a.ChangeXboxAbAddress,
            ChangeXboxLiveAddress = a.ChangeLoginServerAddress && a.ChangeXboxLiveAddress,
            DisableSunsetTimeCheck = a.DisableSunsetTimeCheck,
            DisableSpeedLimit = a.DisableSpeedLimit,
            ChangeAppName = a.ChangeAppName,
            RemoveDrm = a.RemoveDrm,
        };

        if (!a.LoginServerSingleDomainMode)
        {
            options.XalProtocol = (int)a.XalProtocol;
            options.XalHostname = a.XalHostname;
            options.PlayfabApiProtocol = (int)a.PlayfabApiProtocol;
            options.PlayfabApiHostname = a.PlayfabApiHostname;
            options.XboxABProtocol = (int)a.XboxAbProtocol;
            options.XboxABHostname = a.XboxAbHostname;
            options.XboxLiveProtocol = (int)a.XboxLiveProtocol;
            options.XboxLiveHostname = a.XboxLiveHostname;
        }

        return IpaProcessor.Run(options);
    }


    private static IEnumerable<string> GetAndroidPatches(AndroidRequest a)
    {
        yield return "fix-official-msa-login-after-signature-change";
        if (a.DisableSunsetTimeCheck) yield return "disable-sunset-time-check";
        if (a.DisableLicenseCheck) yield return "disable-license-check";
        if (a.DisableTelemetry) yield return "disable-telemetry";
        if (a.DisableMsaLoginSignatureValidation) yield return "disable-msa-login-signature-validation";
        if (a.ChangeLocatorAddress) yield return "change-locator-address";
        if (a.ChangeAppName) yield return "change-app-name";
        if (a.ChangePackageName) yield return "change-package-name";
        if (a.ChangeMsaLoginServiceAddress) yield return "change-msa-login-address";
        if (a.ChangePlayfabApiAddress) yield return "change-playfab-address";
        if (a.ChangeXboxAbAddress) yield return "change-xboxab-address";
        if (a.ChangeXboxLiveAddress)
        {
            yield return "change-xboxlive-address-base";
            yield return "change-xboxlive-address-extra";
        }
        yield return "add-arcore-apikey"; // must run after change-package-name
    }

    private static IEnumerable<string> GetAndroidVariables(AndroidRequest a)
    {
        if (a.ChangeLocatorAddress)
        {
            yield return $"locatorprotocol={a.LocatorProtocol.ToProtocolString()}://";
            yield return $"locatorhostname={a.LocatorHostname}";
        }

        if (a.ChangeAppName)
        {
            yield return $"app_name={a.AppName}";
            yield return $"app_name_short={a.AppNameShort}";
        }

        if (a.ChangePackageName)
        {
            yield return $"package_name={a.PackageName}";
        }

        if (a.LoginServerSingleDomainMode)
        {
            if (a.ChangeMsaLoginServiceAddress)
            {
                yield return $"liveprotocol={a.LoginServerProtocol.ToProtocolString()}://{a.LoginServerHostname}/";
                yield return "livehostname=live.com";
            }
            if (a.ChangePlayfabApiAddress)
            {
                yield return $"playfabprotocol={a.LoginServerProtocol.ToProtocolString()}://{a.LoginServerHostname}/";
                yield return "playfabhostname=playfabapi.com";
            }
            if (a.ChangeXboxAbAddress)
            {
                yield return $"xboxabprotocol={a.LoginServerProtocol.ToProtocolString()}://{a.LoginServerHostname}/";
                yield return "xboxabhostname=xboxab.com";
            }
            if (a.ChangeXboxLiveAddress)
            {
                yield return $"xboxliveprotocol={a.LoginServerProtocol.ToProtocolString()}://{a.LoginServerHostname}/";
                yield return "xboxlivehostname=xboxlive.com";
            }
        }
        else
        {
            if (a.ChangeMsaLoginServiceAddress)
            {
                yield return $"liveprotocol={a.MsaLoginServiceProtocol.ToProtocolString()}://";
                yield return $"livehostname={a.MsaLoginServiceHostname}";
            }
            if (a.ChangePlayfabApiAddress)
            {
                yield return $"playfabprotocol={a.PlayfabApiProtocol.ToProtocolString()}://";
                yield return $"playfabhostname={a.PlayfabApiHostname}";
            }
            if (a.ChangeXboxAbAddress)
            {
                yield return $"xboxabprotocol={a.XboxAbProtocol.ToProtocolString()}://";
                yield return $"xboxabhostname={a.XboxAbHostname}";
            }
            if (a.ChangeXboxLiveAddress)
            {
                yield return $"xboxliveprotocol={a.XboxLiveProtocol.ToProtocolString()}://";
                yield return $"xboxlivehostname={a.XboxLiveHostname}";
            }
        }
    }

    private static string BuildOutputFileName(PatchRequest request)
    {
        var name = request.Platform == PatchPlatform.Android
            ? (request.Android?.AppName ?? request.OutputBaseName)
            : (request.Ios?.AppName ?? request.OutputBaseName);

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Apace";
        }

        return request.Platform == PatchPlatform.Android ? $"{name}.apk" : $"{name}.ipa";
    }

    private static void Validate(PatchRequest request)
    {
        if (!File.Exists(request.InputPath))
        {
            throw new ArgumentException("The uploaded input file no longer exists.");
        }

        if (request.Platform == PatchPlatform.Android)
        {
            var a = request.Android ?? throw new ArgumentException("Android options are required for an Android patch.");

            if (a.AndroidVersion is < 7)
            {
                throw new ArgumentException("Android version must be at least 7.");
            }

            if (a.ChangeLocatorAddress && string.IsNullOrWhiteSpace(a.LocatorHostname))
            {
                throw new ArgumentException("Enter the API server address (hostname:port) first.");
            }

            ValidateLoginServerAddress(a.LoginServerSingleDomainMode, a.ChangeMsaLoginServiceAddress,
                a.LoginServerHostname, a.MsaLoginServiceHostname, "MSA login");
            ValidateLoginServerAddress(a.LoginServerSingleDomainMode, a.ChangePlayfabApiAddress,
                a.LoginServerHostname, a.PlayfabApiHostname, "PlayFab API");
            ValidateLoginServerAddress(a.LoginServerSingleDomainMode, a.ChangeXboxAbAddress,
                a.LoginServerHostname, a.XboxAbHostname, "XboxAB");
            ValidateLoginServerAddress(a.LoginServerSingleDomainMode, a.ChangeXboxLiveAddress,
                a.LoginServerHostname, a.XboxLiveHostname, "Xbox Live");

            if (a.ChangeAppName && string.IsNullOrWhiteSpace(a.AppName))
            {
                throw new ArgumentException("Enter the app name first.");
            }

            if (a.ChangePackageName && string.IsNullOrWhiteSpace(a.PackageName))
            {
                throw new ArgumentException("Enter the package name first.");
            }
        }
        else
        {
            ValidateIos(request.Ios);
        }
    }

    private static void ValidateIos(IosRequest? a)
    {
        if (a is null)
        {
            throw new ArgumentException("iOS options are required for an iOS patch.");
        }

        if (a.ChangeLocatorAddress && string.IsNullOrWhiteSpace(a.LocatorHostname))
        {
            throw new ArgumentException("Enter the locator (API server) address first.");
        }

        if (!a.ChangeLoginServerAddress)
        {
            return;
        }

        if (a.LoginServerSingleDomainMode)
        {
            if ((a.ChangeXalAuthAddress || a.ChangePlayfabApiAddress || a.ChangeXboxAbAddress || a.ChangeXboxLiveAddress) &&
                string.IsNullOrWhiteSpace(a.LoginServerHostname))
            {
                throw new ArgumentException("Enter the auth server address first.");
            }

            return;
        }

        if (a.ChangeXalAuthAddress && string.IsNullOrWhiteSpace(a.XalHostname))
        {
            throw new ArgumentException("Enter the XAL auth hostname/IP first.");
        }
        if (a.ChangePlayfabApiAddress && string.IsNullOrWhiteSpace(a.PlayfabApiHostname))
        {
            throw new ArgumentException("Enter the PlayFab API hostname/IP first.");
        }
        if (a.ChangeXboxAbAddress && string.IsNullOrWhiteSpace(a.XboxAbHostname))
        {
            throw new ArgumentException("Enter the XboxAB hostname/IP first.");
        }
        if (a.ChangeXboxLiveAddress && string.IsNullOrWhiteSpace(a.XboxLiveHostname))
        {
            throw new ArgumentException("Enter the Xbox Live hostname/IP first.");
        }
    }

    /// <summary>
    /// In multi-domain mode the login services get real hostnames, and (like
    /// the original patcher) they must not be bare IP addresses, because the
    /// patches rewrite hostnames inside the app.
    /// </summary>
    private static void ValidateLoginServerAddress(bool singleDomain, bool changed, string singleDomainHost, string host, string service)
    {
        if (!changed)
        {
            return;
        }

        var value = singleDomain ? singleDomainHost : host;

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Enter the {service} server address first.");
        }

        if (!singleDomain && IPAddress.TryParse(value.Split(':')[0], out _))
        {
            throw new ArgumentException($"{service} address cannot be an IP in multi-domain mode - use a hostname (single-domain mode has no such limitation).");
        }
    }

    private static void EnsureWorkDir()
    {
        Directory.CreateDirectory(WorkDir);

        var sourcePatches = Path.Combine(AppContext.BaseDirectory, "Patches");
        var targetPatches = Path.Combine(WorkDir, "Patches");

        if (!Directory.Exists(sourcePatches))
        {
            throw new DirectoryNotFoundException(
                $"Patch definitions not found at '{sourcePatches}'. The Minecraft_Earth_Patcher repo (repos/Minecraft_Earth_Patcher) must be present and built.");
        }

        // keep the patch definitions in sync with the build
        U.CopyDir(sourcePatches, targetPatches, overwrite: true);
    }

    private static string? ResolveIconPath(PatchJob job)
    {
        if (!File.Exists(DefaultIconPath))
        {
            job.AddLog($"{DateTimeOffset.Now:HH:mm:ss} [WRN] Apace icon not found at '{DefaultIconPath}' - the app icon will not be changed.");
            return null;
        }

        return DefaultIconPath;
    }

    private static bool IsJavaAvailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo("java", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void CleanupJobWorkingFiles(PatchJob job)
    {
        try
        {
            Directory.Delete(Path.Combine(job.WorkDir, "decoded"), true);
        }
        catch
        {
        }

        try
        {
            if (job.IsFinished && File.Exists(job.InputPath))
            {
                File.Delete(job.InputPath);
            }
        }
        catch
        {
        }
    }
}
