using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using MCEPatcher.Core;
using Serilog;

namespace Apace.LauncherUI.Patcher;

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
    private readonly ServerManager serverManager;

    public PatcherService(ServerManager serverManager)
    {
        this.serverManager = serverManager;
    }

    public static string BaseDir => Path.Combine(Program.DataDir, "patcher");

    public static string WorkDir => Path.Combine(BaseDir, "work");

    /// <summary>
    /// aapt2 (run by apktool) cannot open non-ASCII paths on Windows, so when the
    /// install directory contains e.g. Cyrillic characters, jobs run from an ASCII temp path.
    /// </summary>
    public static string JobsDir
    {
        get
        {
            string defaultDir = Path.Combine(BaseDir, "jobs");
            if (IsAscii(Path.GetFullPath(defaultDir)))
            {
                return defaultDir;
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "ApacePatcher", "jobs");
            if (IsAscii(tempDir))
            {
                return tempDir;
            }

            string rootDir = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "ApacePatcher", "jobs");
            return rootDir;
        }
    }

    private static bool IsAscii(string path) => path.All(c => c < 128);

    /// <summary>Default Apace icon shipped with the panel (images/Apace_Favicon.png).</summary>
    public static string DefaultIconPath => Path.Combine(AppContext.BaseDirectory, "images", "Apace_Favicon.png");

    /// <summary>True while any job is queued or running - the whole panel is locked to the patcher page then.</summary>
    public bool AnyJobActive
    {
        get
        {
            lock (jobsLock)
            {
                return jobs.Any(job => job.Status is PatchJobStatus.Queued or PatchJobStatus.Running);
            }
        }
    }

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
    /// The input file's availability is verified (and, if needed, waited for) by the
    /// background job itself - see <see cref="VerifyInputFileAsync"/>.
    /// </summary>
    public PatchJob CreateJob(PatchRequest request)
    {
        // Patching requires the Earth server to be fully offline: MCEPatcher.Core
        // swaps the process working directory and global tool state, which breaks
        // the running server components (DB access etc.) until the job finishes.
        if (serverManager.Status is not ServerStatus.Offline)
        {
            throw new InvalidOperationException(
                "The Earth server must be offline before patching a client - stop it first.");
        }

        Validate(request);

        var job = new PatchJob
        {
            Mode = request.Mode,
            Platform = request.Platform,
            WorkDir = Path.Combine(JobsDir, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}"),
            InputPath = request.InputPath,
            InputFileName = request.InputFileName,
            ResourcePackPath = request.ResourcePackPath,
        };

        Directory.CreateDirectory(job.WorkDir);

        // Held from queueing until the job finishes: blocks starting the Earth
        // server/components while the patcher owns the process working directory.
        var patchLock = serverManager.AcquirePatchLock();

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

        _ = RunJobAsync(job, request, patchLock);

        return job;
    }

    private async Task RunJobAsync(PatchJob job, PatchRequest request, IDisposable patchLock)
    {
        await gate.WaitAsync();

        var originalDir = Environment.CurrentDirectory;

        job.MarkRunning();

        try
        {
            EnsureWorkDir();
            Environment.CurrentDirectory = WorkDir;
            PatchLogSink.CurrentJob = job;

            job.AddLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INF] Starting {job.Platform} patch ({job.Mode} mode)");

            // Make sure the uploaded input is really on disk and readable BEFORE
            // apktool is launched. The upload and the job start are two independent
            // client calls (XHR to /api/patcher/upload* vs. this call), and the page
            // that uploaded the file used to delete it again when its circuit was
            // torn down (the Generate anchor navigates that page away) - which made
            // the FIRST patcher run after a server start die with apktool's cryptic
            // "INPUT FILE ... WAS NOT FOUND OR WAS NOT READABLE". This verifies the
            // file (waiting for a racing upload if needed) and, when it is really
            // gone, fails the job with a precise message of its own.
            await VerifyInputFileAsync(job);

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

            job.AddLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INF] Done: {Path.GetFileName(outputPath)}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Patch job {JobId} failed", job.Id);
            job.AddLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [ERR] {ex.Message}");
            job.MarkFailed(ex.Message);
        }
        finally
        {
            PatchLogSink.CurrentJob = null;
            Environment.CurrentDirectory = originalDir;
            gate.Release();
            CleanupJobWorkingFiles(job);
            patchLock.Dispose();
        }
    }

    /// <summary>
    /// Completed uploads the server has fully written to disk: final path ->
    /// (exact size, when it finished). Filled by the upload endpoints
    /// (PatcherController) once a file is completely received, so that job start
    /// can verify its input against authoritative data.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (long Size, DateTimeOffset CompletedAt)> completedUploads =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records a fully-written upload (called by the upload endpoints once the file is complete).</summary>
    public static void RegisterCompletedUpload(string path, long size)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            completedUploads[path] = (size, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Forgets an upload record (the file was deleted or consumed by a finished job).</summary>
    public static void DeregisterUpload(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            completedUploads.TryRemove(path, out _);
        }
    }

    /// <summary>Size/completion time of a fully-received upload, if this path is one.</summary>
    public static bool TryGetCompletedUpload(string path, out long size, out DateTimeOffset completedAt)
    {
        if (completedUploads.TryGetValue(path ?? "", out var record))
        {
            size = record.Size;
            completedAt = record.CompletedAt;
            return true;
        }

        size = 0;
        completedAt = default;
        return false;
    }

    /// <summary>
    /// True while any queued or running job still needs this file (as its input
    /// APK/IPA or its resource pack). Pages must not delete such a file - the
    /// job will read it long after the page that uploaded it is gone.
    /// </summary>
    public bool IsJobFilePath(string path)
    {
        lock (jobsLock)
        {
            return jobs.Any(job => !job.IsFinished
                && (string.Equals(job.InputPath, path, StringComparison.OrdinalIgnoreCase)
                    || (job.ResourcePackPath is not null
                        && string.Equals(job.ResourcePackPath, path, StringComparison.OrdinalIgnoreCase))));
        }
    }

    /// <summary>
    /// Verifies the job's input file is present and readable before apktool is
    /// launched, so a lost upload never surfaces as apktool's cryptic
    /// "INPUT FILE (...) WAS NOT FOUND OR WAS NOT READABLE".
    ///
    /// The upload and the job start are two independent client calls, so a
    /// racing client may start the job while the file is still being written:
    /// when the input is not usable yet, the job waits (bounded, 60 s) for it
    /// to appear and stop growing instead of failing immediately. Only when it
    /// never becomes readable does the job fail with a precise message.
    /// </summary>
    private static async Task VerifyInputFileAsync(PatchJob job)
    {
        const int TimeoutSeconds = 60;
        const int PollMilliseconds = 500;

        var path = job.InputPath;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(TimeoutSeconds);
        var announcedWait = false;
        long lastLength = -1;

        while (true)
        {
            if (TryMeasureReadableLength(path, out var length))
            {
                // the upload endpoints register the exact final size once a
                // file is fully received - a matching length is authoritative
                if (TryGetCompletedUpload(path, out var expected, out _) && length == expected)
                {
                    job.AddLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INF] Input file ready: {length} bytes.");
                    return;
                }

                // unknown size (e.g. a third-party client): require the length
                // to stop growing across two consecutive polls
                if (length > 0 && length == lastLength)
                {
                    job.AddLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INF] Input file ready: {length} bytes.");
                    return;
                }

                lastLength = length;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }

            if (!announcedWait)
            {
                announcedWait = true;
                job.AddLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INF] The uploaded input file is not readable yet - waiting up to {TimeoutSeconds} s for the upload to finish...");
            }

            await Task.Delay(PollMilliseconds);
        }

        throw TryGetCompletedUpload(path, out _, out var completedAt)
            ? new InvalidOperationException(
                $"The uploaded input file '{path}' (fully received at {completedAt:yyyy-MM-dd HH:mm:ss} UTC) is missing or unreadable - it was most likely deleted while the job was starting (e.g. the uploading page was closed or removed the file before the patch could begin). Re-upload the original file and start the job again.")
            : new InvalidOperationException(
                $"The uploaded input file '{path}' was not found or was not readable within {TimeoutSeconds} s - the upload may have failed or the file was removed. Re-upload the original file and start the job again.");
    }

    /// <summary>Opens the file for reading to prove it exists AND is readable; false if it does not (yet).</summary>
    private static bool TryMeasureReadableLength(string path, out long length)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            length = fs.Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            length = 0;
            return false;
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
            job.AddLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INF] Using system aapt2 ({aapt2}) instead of the one bundled with apktool.");
        }

        if (SystemTools.ZipAlignPath is { } zipAlign)
        {
            job.AddLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INF] Using system zipalign ({zipAlign}) instead of the Google build-tools one.");
        }

        if (SystemTools.ApktoolFramePath is { } framePath)
        {
            job.AddLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [INF] Using framework directory ({framePath}) for apktool decode/build.");
        }

        using (var fs = File.OpenRead(job.InputPath))
        {
            if (!ApkProcessor.VerifyApkHash(fs))
            {
                // Hard reject (default): a hash mismatch means the upload is not
                // the supported ORIGINAL Minecraft Earth APK. An already-patched
                // APK (re-signed by any patcher) or a re-packed mirror decodes
                // fine for minutes and then dies in apktool with a cryptic smali
                // error (the field-reported Interop.smali "missing EOF at
                // '.param'"). Fail the job NOW with the actual reason instead.
                // The settings toggle "Reject non-original APK/IPA uploads"
                // restores the old warning-only behavior for operators who
                // knowingly patch a different build.
                if (Settings.Instance.PatcherRejectNonOriginalApk is not false)
                {
                    throw new InvalidOperationException(
                        "The uploaded APK is NOT the supported original Minecraft Earth APK (SHA-256 mismatch). " +
                        "Already-patched APKs cannot be patched again and re-packed mirrors break the client — " +
                        "upload the original com.mojang.minecraftearth 0.33.0 APK you got from Google Play. " +
                        "The check can be disabled in the panel settings (\"Reject non-original APK/IPA uploads\").");
                }

                job.AddLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [WRN] The .apk file hash does not match the supported original - patching may fail (hard rejection disabled in the settings).");
            }
        }

        if (request.ResourcePackPath is not null)
        {
            using (var fs = File.OpenRead(request.ResourcePackPath))
            {
                if (!ApkProcessor.VerifyResourcePackHash(fs))
                {
                    job.AddLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [WRN] The resource pack hash does not match - the game may not function correctly.");
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
                if (Settings.Instance.PatcherRejectNonOriginalApk is not false)
                {
                    throw new InvalidOperationException(
                        "The uploaded IPA is NOT the supported original Minecraft Earth IPA (hash mismatch). " +
                        "Already-patched IPAs cannot be patched again — upload the original Minecraft Earth 0.33.0 IPA. " +
                        "The check can be disabled in the panel settings (\"Reject non-original APK/IPA uploads\").");
                }

                job.AddLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [WRN] The .ipa file hash does not match the supported original - patching may fail (hard rejection disabled in the settings).");
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
            ChangeBundleId = a.ChangeBundleId,
            BundleId = a.BundleId,
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
        yield return "fix-qr-scanner";
        if (a.FixQrPermissionContext) yield return "fix-qr-permission-context";
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
        if (string.IsNullOrWhiteSpace(request.InputPath))
        {
            throw new ArgumentException("An uploaded input file is required.");
        }

        // NOTE: the input file's existence is deliberately NOT checked here. The
        // upload and the job start are two independent client calls that can race
        // (a client may start the job before its upload request finishes), so the
        // job itself verifies - and if needed waits for - the file (see
        // VerifyInputFileAsync) and reports a precise error instead of failing the
        // request outright while the upload is still in flight.

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

        if (a.ChangeBundleId && !IpaProcessor.IsValidBundleId(a.BundleId))
        {
            throw new ArgumentException("Enter a valid bundle ID first (letters, digits, '-' and '.', e.g. com.example.app).");
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
            job.AddLog($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [WRN] Apace icon not found at '{DefaultIconPath}' - the app icon will not be changed.");
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

        // the job consumed its uploads - forget them in the completed-upload
        // registry so it cannot grow without bound
        DeregisterUpload(job.InputPath);

        if (job.ResourcePackPath is not null)
        {
            DeregisterUpload(job.ResourcePackPath);
        }
    }
}
