using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Serilog;
using Apace.Common.Utils;
using ILogger = Serilog.ILogger;

namespace Apace.LauncherUI.Utils;

public enum SelfUpdateMode
{
    None,
    Docker,
    BareMetal,
}

public enum SelfUpdateState
{
    Idle,
    Running,
    /// <summary>The last step hands the stack over to a fresh container — this panel is about to die.</summary>
    Recreating,
    Completed,
    /// <summary>New files are on disk, but the running panel is still the old build — a restart applies it.</summary>
    NeedsRestart,
    Failed,
}

/// <summary>
/// One-click self-update driven from the panel's About page, in two flavours:
///
/// - <see cref="SelfUpdateMode.Docker"/>: the panel container talks to the host
///   daemon through the mounted docker socket and runs
///   "docker compose pull && docker compose up -d" on the very stack it is part
///   of. Data survives because every persistent path is a bind mount of the
///   stock persistent root — which is exactly what the safety checks verify
///   before anything is recreated. The panel container is replaced as the last
///   step, so the process (and this circuit) is expected to die mid-flow.
///
/// - <see cref="SelfUpdateMode.BareMetal"/>: release-channel zip installs.
///   Stops the server components, downloads the platform asset of the newest
///   GitHub release and unzips it over the install root, skipping every
///   runtime-data location. The running panel keeps the old build in memory
///   until it is restarted.
///
/// Everything destructive is logged line by line and streamed to the UI via
/// <see cref="OnLogLine"/>. A failed run must leave a working (old) install:
/// the docker flow pulls before it recreates, the bare-metal flow checks that
/// no target file is locked before it writes anything.
/// </summary>
public sealed class SelfUpdateService
{
    public event Action? OnStateChanged;
    public event Action<string>? OnLogLine;

    private static readonly HttpClient httpClient = CreateHttpClient();

    private const string ApiBase = "https://api.github.com/repos/KotPasztet/Apace";

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // Per-call timeouts are enforced with linked cancellation tokens (the
        // body downloads need their own, longer budget than requests).
        client.DefaultRequestHeaders.UserAgent.TryParseAdd($"KotPasztet/Apace/{typeof(SelfUpdateService).Assembly.GetName().Version}");
        return client;
    }

    // ── Docker mode ──
    private const string DockerEnvMarker = "/.dockerenv";
    private const string DockerSocketPath = "/var/run/docker.sock";
    private const string ComposeDir = "/app/compose";
    private const string DefaultBridgePort = "19132";

    // The stock persistent root every docker-compose*.yml in the repo mounts.
    // The baked-in compose files recreate the stack with THESE paths, so an
    // update is only safe when the running stack actually uses them.
    private const string StockPersistentRoot = "/opt/apace-persistent";

    // /app/<destination> → expected host path in the stock layout. This mirrors
    // the volumes: list of the baked-in compose files exactly, so "compose up -d"
    // can only reproduce the mounts the running stack already has.
    private static readonly Dictionary<string, string> ExpectedStockMounts = new()
    {
        ["/app/launcher/config.json"] = $"{StockPersistentRoot}/config.json",
        ["/app/launcher/Data"] = $"{StockPersistentRoot}/launcher-data",
        ["/app/launcher/logs"] = $"{StockPersistentRoot}/launcher-logs",
        ["/app/data"] = $"{StockPersistentRoot}/data",
        ["/app/logs"] = $"{StockPersistentRoot}/logs",
        ["/root/.aspnet/DataProtection-Keys"] = $"{StockPersistentRoot}/dataprotection-keys",
        ["/app/staticdata/resourcepacks"] = $"{StockPersistentRoot}/resourcepacks",
        ["/app/staticdata/server_template_dir"] = $"{StockPersistentRoot}/server-template-dir",
        ["/app/launcher/persistent_fabric"] = $"{StockPersistentRoot}/fabric-data",
        ["/app/components/api_config.json"] = $"{StockPersistentRoot}/api-config/api_config.json",
    };

    // Mounts probed through /proc/self/mountinfo in the constructor (cheap, no
    // docker call): the panel's own config and the two data roots.
    private static readonly string[] MountProbeDestinations =
    [
        "/app/launcher/config.json",
        "/app/launcher/Data",
        "/app/data",
    ];

    // Host ports the baked-in compose files publish ("container/proto" → host port).
    private static readonly Dictionary<string, string> ExpectedStockPorts = new()
    {
        ["5000/tcp"] = "5000",
        ["1808/tcp"] = "1808",
        ["5532/tcp"] = "5532",
    };

    private static readonly TimeSpan PullTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan UpTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    // ── Bare-metal mode ──
    private const string ProbeFileName = ".apace-selfupdate-probe";

    // Only these top-level entries of a release zip are ever extracted.
    private static readonly HashSet<string> AllowedRoots = new(StringComparer.OrdinalIgnoreCase) { "components", "launcher", "staticdata" };
    private static readonly HashSet<string> AllowedRootFiles = new(StringComparer.OrdinalIgnoreCase) { "run_launcher.ps1" };

    // Runtime data that must never be touched, even inside the allowed roots.
    private static readonly string[] ExcludedPrefixes =
    [
        "launcher/Data/",
        "launcher/logs/",
        "launcher/persistent_fabric/",
        "staticdata/resourcepacks/",
    ];

    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "launcher/config.json",         // the panel's own settings
        "components/api_config.json",   // the ApiServer login secrets
    };

    private const int MaxLogLines = 400;

    private readonly UpdateCheckService updateCheck;
    private readonly ServerManager? serverManager;
    // "Serilog." is needed because the instance method Log(string) below shadows
    // the Serilog.Log class for simple-name lookup inside this type.
    private readonly ILogger logger = Serilog.Log.Logger;
    private readonly Lock logLock = new();
    private readonly List<string> logLines = [];
    private int _running;

    public SelfUpdateService(UpdateCheckService updateCheckService, ServerManager? serverManager = null)
    {
        updateCheck = updateCheckService;
        this.serverManager = serverManager;

        if (File.Exists(DockerEnvMarker))
        {
            Mode = SelfUpdateMode.Docker;
            UnavailableReason = CheckDockerGuards();
        }
        else
        {
            Mode = SelfUpdateMode.BareMetal;
            UnavailableReason = CheckBareMetalGuards();
        }

        if (UnavailableReason is not null)
        {
            Mode = SelfUpdateMode.None;
        }
    }

    /// <summary>Which in-place update flavour this install supports (None → instructions only).</summary>
    public SelfUpdateMode Mode { get; private set; }

    public bool CanSelfUpdate => Mode != SelfUpdateMode.None;

    /// <summary>Why one-click updating is unavailable (null when it is available).</summary>
    public string? UnavailableReason { get; }

    public SelfUpdateState State { get; private set; } = SelfUpdateState.Idle;

    /// <summary>Failure reason of the last run (Failed state).</summary>
    public string? Error { get; private set; }

    /// <summary>Success message of the last run (Completed / NeedsRestart state).</summary>
    public string? Result { get; private set; }

    /// <summary>0..1 while the bare-metal flow downloads the release zip, null otherwise.</summary>
    public float? DownloadProgress { get; private set; }

    public bool IsBusy => State is SelfUpdateState.Running or SelfUpdateState.Recreating;

    /// <summary>8-char form for UI and logs; "(unknown)" when no SHA is available.</summary>
    public static string ShortSha(string? sha) => UpdateCheckService.ShortSha(sha);

    // ── Guards ────────────────────────────────────────────────────────────

    private string? CheckDockerGuards()
    {
        if (!File.Exists(DockerSocketPath))
        {
            return "the docker socket is not mounted into this container";
        }

        if (ResolveDockerCli() is null)
        {
            return "the docker CLI is not installed in this image — update the image once from the host, then this works";
        }

        if (!File.Exists(ComposeFilePath()))
        {
            return $"no compose file baked into this image ({ComposeFilePath()} is missing) — update the image once from the host";
        }

        // Stock-layout check: if the persistent paths are not bind mounts of the
        // stock root, "compose up -d" from the baked-in file would recreate the
        // stack with FRESH volume paths (i.e. empty data). Refuse instead.
        foreach (var destination in MountProbeDestinations)
        {
            var hostPath = GetBindMountHostPath(destination);
            if (hostPath is null)
            {
                return $"{destination} is not a bind mount — this stack was not started from a compose file with volumes";
            }

            if (!hostPath.StartsWith(StockPersistentRoot + "/", StringComparison.Ordinal))
            {
                return $"{destination} is mounted from '{hostPath}', not the stock '{StockPersistentRoot}' layout — update from the host instead";
            }
        }

        return null;
    }

    private string? CheckBareMetalGuards()
    {
        if (updateCheck.Channel != "release")
        {
            return $"no release zips are published for the '{updateCheck.Channel}' channel";
        }

        if (InstallRoot is null)
        {
            return "could not locate the Apace install root (expected 'launcher' + 'components' next to the panel directory)";
        }

        try
        {
            var probe = Path.Combine(InstallRoot, ProbeFileName);
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return $"the install directory is not writable ({ex.Message})";
        }

        return null;
    }

    /// <summary>
    /// Install root of a bare-metal install: the directory that holds
    /// 'launcher/' and 'components/' (the parent of the panel directory).
    /// </summary>
    private string? installRoot;

    private string? InstallRoot
    {
        get
        {
            if (installRoot is not null)
            {
                return installRoot;
            }

            try
            {
                var launcherDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
                var root = Directory.GetParent(launcherDir)?.FullName;

                if (root is not null
                    && Directory.Exists(Path.Combine(root, "launcher"))
                    && Directory.Exists(Path.Combine(root, "components")))
                {
                    installRoot = root;
                    return installRoot;
                }
            }
            catch
            {
                // fall through to null
            }

            return null;
        }
    }

    private string ComposeFilePath()
        => Path.Combine(ComposeDir, updateCheck.Channel == "dev" ? "docker-compose.dev.yml" : "docker-compose.yml");

    // ── Docker flow ───────────────────────────────────────────────────────

    private async Task BeginDockerAsync(CancellationToken cancellationToken)
    {
        var docker = ResolveDockerCli()!;
        var composeFile = ComposeFilePath();

        Log($"Running self-update ({updateCheck.Channel} channel, {ShortSha(updateCheck.CurrentSha)} → {ShortSha(updateCheck.LatestSha)})");

        // Identify the running stack so compose re-creates THIS container (same
        // project) instead of trying to start a second one next to it.
        var project = await ResolveComposeProjectAsync(docker, cancellationToken);

        var envFile = Path.Combine(Path.GetTempPath(), $"apace-selfupdate-{Guid.NewGuid():N}.env");
        await File.WriteAllTextAsync(envFile, $"BRIDGE_PORT={Environment.GetEnvironmentVariable("BRIDGE_PORT") ?? DefaultBridgePort}\nAPACE_CHANNEL={updateCheck.Channel}\n", cancellationToken);

        try
        {
            Log($"docker compose -p {project} -f {composeFile} pull");
            var pullExit = await RunProcessAsync(docker, ["compose", "-p", project, "-f", composeFile, "--env-file", envFile, "pull"], PullTimeout, cancellationToken);
            if (pullExit != 0)
            {
                Fail("'docker compose pull' failed — nothing was changed, the previous container keeps running");
                return;
            }

            // The next command replaces the container this process lives in.
            Log("");
            Log("Recreating container — the panel will be back in ~a minute at the same address.");
            Log("This page will disconnect; all data is preserved (volume mounts).");
            State = SelfUpdateState.Recreating;
            Notify();

            var upExit = await RunProcessAsync(docker, ["compose", "-p", project, "-f", composeFile, "--env-file", envFile, "up", "-d"], UpTimeout, cancellationToken);

            // If this line is ever reached, the panel was NOT replaced (or died
            // after the command finished).
            if (upExit == 0)
            {
                Complete("Update applied — the container was recreated. Reload this page in ~a minute.");
            }
            else
            {
                Fail($"'docker compose up -d' exited with code {upExit} — the stack may be mid-recreation; check 'docker compose ps' on the host");
            }
        }
        finally
        {
            try { File.Delete(envFile); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Returns the compose project of the running panel container, after
    /// verifying that its published ports and bind mounts match the stock
    /// layout the baked-in compose file will recreate.
    /// </summary>
    private async Task<string> ResolveComposeProjectAsync(string docker, CancellationToken cancellationToken)
    {
        var container = ReadContainerId();
        var output = await CaptureProcessOutputAsync(docker, ["inspect", container], ApiTimeout, cancellationToken);

        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"'docker inspect {container}' found no container — cannot self-update safely");
        }

        var inspected = document.RootElement[0];

        var project = inspected.GetString("Config", "Labels", "com.docker.compose.project");
        if (string.IsNullOrEmpty(project))
        {
            throw new InvalidOperationException("this container was not started by docker compose (no project label) — update from the host instead");
        }

        // Ports: recreating with different host ports would break the panel URL
        // or fail on a conflict — refuse when the mapping drifted from stock.
        var bridgePort = Environment.GetEnvironmentVariable("BRIDGE_PORT") ?? DefaultBridgePort;
        var expectedPorts = new Dictionary<string, string>(ExpectedStockPorts)
        {
            [$"{bridgePort}/udp"] = bridgePort,
        };

        var bindings = inspected.GetElement("HostConfig", "PortBindings");
        if (bindings.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("this container publishes no ports — not a stock layout, update from the host instead");
        }

        var actualPorts = new Dictionary<string, string>();
        foreach (var binding in bindings.EnumerateObject())
        {
            var hostPort = binding.Value.ValueKind == JsonValueKind.Array && binding.Value.GetArrayLength() > 0
                ? binding.Value[0].GetString("HostPort")
                : null;

            if (hostPort is not null)
            {
                actualPorts[binding.Name] = hostPort;
            }
        }

        if (!actualPorts.OrderBy(p => p.Key).SequenceEqual(expectedPorts.OrderBy(p => p.Key)))
        {
            throw new InvalidOperationException(
                $"the published ports drifted from the stock layout ({Describe(actualPorts)} instead of {Describe(expectedPorts)}) — update from the host instead");
        }

        // Mounts: the host paths must be exactly the stock ones (the constructor
        // already cross-checked this through /proc/self/mountinfo).
        var mounts = inspected.GetElement("Mounts");
        if (mounts.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("this container has no mounts — not a stock layout, update from the host instead");
        }

        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mount in mounts.EnumerateArray())
        {
            var destination = mount.GetString("Destination");
            var source = mount.GetString("Source");
            if (destination is not null && source is not null)
            {
                sources[destination] = source;
            }
        }

        foreach (var (destination, expectedSource) in ExpectedStockMounts)
        {
            if (!sources.TryGetValue(destination, out var source))
            {
                throw new InvalidOperationException($"'{destination}' is not mounted — not a stock layout, update from the host instead");
            }

            if (!string.Equals(source, expectedSource, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"'{destination}' is mounted from '{source}', not '{expectedSource}' — update from the host instead");
            }
        }

        Log($"Compose project '{project}', stock layout verified (ports + {ExpectedStockMounts.Count} mounts)");
        return project;
    }

    /// <summary>Container id of this process: docker writes it into /etc/hostname.</summary>
    private static string ReadContainerId()
    {
        try
        {
            var hostname = File.ReadAllText("/etc/hostname").Trim();
            if (hostname.Length > 0)
            {
                return hostname;
            }
        }
        catch
        {
            // fall through
        }

        return Environment.MachineName;
    }

    /// <summary>
    /// Host path a path inside this container is bind-mounted from, read from
    /// /proc/self/mountinfo. Null when the path is not a mount at all.
    /// </summary>
    private static string? GetBindMountHostPath(string containerPath)
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                // "36 35 98:0 /mnt1 /mnt2 rw,noatime master:1 - ext3 /dev/root rw,errors=continue"
                var fields = line.Split(' ');
                if (fields.Length < 10)
                {
                    continue;
                }

                var mountPoint = UnescapeMountInfo(fields[4]);
                if (!string.Equals(mountPoint, containerPath, StringComparison.Ordinal))
                {
                    continue;
                }

                // The root field (path within the source filesystem) carries the
                // full host path for docker bind mounts.
                var root = UnescapeMountInfo(fields[3]);
                if (root.StartsWith('/'))
                {
                    return root;
                }

                return null;
            }
        }
        catch
        {
            // unreadable mountinfo — treat as "not a mount"
        }

        return null;
    }

    private static string UnescapeMountInfo(string value)
        => value
            .Replace("\\040", " ")
            .Replace("\\011", "\t")
            .Replace("\\012", "\n")
            .Replace("\\134", "\\");

    // ── Bare-metal flow ───────────────────────────────────────────────────

    private async Task BeginBareMetalAsync(CancellationToken cancellationToken)
    {
        var installRoot = InstallRoot!;
        Log($"Running self-update (release channel, {ShortSha(updateCheck.CurrentSha)} → {ShortSha(updateCheck.LatestSha)})");

        var (tag, asset) = await FindReleaseAssetAsync(cancellationToken);
        Log($"Latest release {tag}: {asset.Name} ({asset.Size / 1_000_000.0:0} MB)");

        await StopComponentsAsync(cancellationToken);

        var zipPath = Path.Combine(installRoot, $".apace-update-{tag}.zip");
        try
        {
            await DownloadFileAsync(asset.DownloadUrl, zipPath, asset.Size, cancellationToken);
            ApplyZip(zipPath, installRoot, cancellationToken);
        }
        finally
        {
            DownloadProgress = null;
            try { File.Delete(zipPath); } catch { /* best effort */ }
        }

        State = SelfUpdateState.NeedsRestart;
        Result = "Update applied — restart the panel (close this window, then run run.sh / run_launcher.ps1) to run the new build.";
        Notify();
        logger.Information("Self-update applied ({Tag}); a panel restart activates it", tag);
    }

    private async Task StopComponentsAsync(CancellationToken cancellationToken)
    {
        if (serverManager is null)
        {
            return;
        }

        if (serverManager.Status is ServerStatus.Offline && !serverManager.AnyOnline)
        {
            Log("Server components are not running — nothing to stop");
            return;
        }

        Log("Stopping server components (files must not be in use while they are replaced)...");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMinutes(3));

        try
        {
            await serverManager.Stop(timeoutSource.Token);
            Log("Server components stopped");
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("stopping the server components timed out — stop them from the Server Status page and retry");
        }
    }

    private sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

    private static async Task<(string Tag, ReleaseAsset Asset)> FindReleaseAssetAsync(CancellationToken cancellationToken)
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : "linux";

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new InvalidOperationException($"unsupported CPU architecture: {RuntimeInformation.ProcessArchitecture}"),
        };

        var wanted = $"Apace-{os}-{arch}.zip";
        // The Termux asset is a byte-for-byte alias of linux-arm64 (release.yml).
        var fallback = os == "linux" && arch == "arm64" ? "Apace-termux-arm64.zip" : null;

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ApiTimeout);

        using var response = await httpClient.GetAsync($"{ApiBase}/releases/latest", HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"could not fetch the latest release (HTTP {(int)response.StatusCode})");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutSource.Token);

        var tag = document.RootElement.GetProperty("tag_name").GetString() ?? "(unknown tag)";
        var assets = document.RootElement.GetProperty("assets");

        foreach (var candidate in new[] { wanted, fallback })
        {
            if (candidate is null)
            {
                continue;
            }

            foreach (var element in assets.EnumerateArray())
            {
                var name = element.GetProperty("name").GetString();
                if (string.IsNullOrEmpty(name) || !string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var url = element.GetProperty("browser_download_url").GetString();
                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }

                return (tag, new ReleaseAsset(name, url, element.GetProperty("size").GetInt64()));
            }
        }

        var available = assets.EnumerateArray().Select(a => a.GetProperty("name").GetString()).Where(n => !string.IsNullOrEmpty(n));
        throw new InvalidOperationException($"no '{wanted}' asset on {tag} (available: {string.Join(", ", available)})");
    }

    private async Task DownloadFileAsync(string url, string destPath, long expectedSize, CancellationToken cancellationToken)
    {
        Log("Downloading...");
        DownloadProgress = 0;
        Notify();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(DownloadTimeout);

        using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        var lastReport = Stopwatch.StartNew();

        await using var fileStream = File.OpenWriteNew(destPath);
        await using var content = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
        var buffer = new byte[81920];
        long downloaded = 0;
        int read;

        while ((read = await content.ReadAsync(buffer, timeoutSource.Token)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), timeoutSource.Token);
            downloaded += read;

            if (lastReport.ElapsedMilliseconds >= 250)
            {
                DownloadProgress = total > 0 ? (float)downloaded / total.Value : null;
                Notify();
                lastReport.Restart();
            }
        }

        await fileStream.FlushAsync(timeoutSource.Token);
        DownloadProgress = 1;
        Log($"Downloaded {downloaded / 1_000_000.0:0} MB");

        if (downloaded < expectedSize)
        {
            throw new InvalidOperationException($"the downloaded zip is too small ({downloaded} bytes, expected {expectedSize}) — not extracting anything");
        }
    }

    private void ApplyZip(string zipPath, string installRoot, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        // The zips are published from "Compress-Archive build/Release/<profile>/*",
        // so entries sit at the archive root. Normalise the separators (older
        // PowerShell wrote '\'), keep only the code directories and drop every
        // runtime-data location.
        var items = archive.Entries
            .Where(entry => entry.Name.Length > 0)
            .Select(entry => (Entry: entry, RelativePath: entry.FullName.Replace('\\', '/')))
            .Where(item =>
            {
                var path = item.RelativePath;
                var separator = path.IndexOf('/');
                var root = separator < 0 ? path : path[..separator];

                if (separator < 0 ? !AllowedRootFiles.Contains(path) : !AllowedRoots.Contains(root))
                {
                    return false;
                }

                if (ExcludedFiles.Contains(path) || ExcludedPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    return false;
                }

                return true;
            })
            .ToList();

        if (items.Count == 0)
        {
            throw new InvalidOperationException("the release zip contains no installable files");
        }

        // Nothing is modified until every existing target proved writable — a
        // locked file (Windows: the running panel itself) aborts the whole run.
        var locked = new List<string>();
        foreach (var (entry, relativePath) in items)
        {
            var target = Path.Combine(installRoot, relativePath);
            if (!File.Exists(target))
            {
                continue;
            }

            try
            {
                using var _ = File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch
            {
                locked.Add(relativePath);
            }
        }

        if (locked.Count > 0)
        {
            throw new InvalidOperationException(
                $"{locked.Count} file(s) are locked by the running panel (e.g. '{locked[0]}') — in-place updating on Windows needs the panel closed; use the command below instead");
        }

        Log($"Extracting {items.Count} files over {installRoot}");
        Log("(never touched: launcher/Data, launcher/logs, launcher/config.json, data/, logs/, staticdata/resourcepacks, components/api_config.json)");

        var done = 0;
        foreach (var (entry, relativePath) in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var target = Path.Combine(installRoot, relativePath);
            var temp = target + ".apace-new";

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // Extract next to the target and move over it: the rename keeps
                // the old inode alive for the still-running panel process.
                entry.ExtractToFile(temp, overwrite: true);
                File.Move(temp, target, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch { /* best effort */ }

                throw;
            }

            done++;
            if (done % 250 == 0)
            {
                Log($"  {done}/{items.Count} files extracted");
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            MakeExecutablesUnix(installRoot, items);
        }

        Log($"Extracted {done} files");
    }

    private static void MakeExecutablesUnix(string installRoot, List<(ZipArchiveEntry Entry, string RelativePath)> items)
    {
        const UnixFileMode executeBits = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        foreach (var (_, relativePath) in items)
        {
            var isExecutable = relativePath switch
            {
                "launcher/Launcher" => true,
                "run_launcher.ps1" => true,
                _ => relativePath.StartsWith("components/", StringComparison.Ordinal)
                    && Path.GetExtension(relativePath) == string.Empty
                    && !relativePath.EndsWith(".apace-new", StringComparison.Ordinal),
            };

            if (!isExecutable)
            {
                continue;
            }

            var path = Path.Combine(installRoot, relativePath);
            try
            {
                if (File.Exists(path))
                {
#pragma warning disable CA1416 // Unix-only APIs; the caller guards with !OperatingSystem.IsWindows()
                    File.SetUnixFileMode(path, File.GetUnixFileMode(path) | executeBits);
#pragma warning restore CA1416
                }
            }
            catch
            {
                // best effort — a non-executable component is reported by the
                // normal file validation on the next start
            }
        }
    }

    // ── Public entry point ────────────────────────────────────────────────

    /// <summary>
    /// Starts the update in the background. Single flight: repeated calls while
    /// a run is in progress are ignored.
    /// </summary>
    public void Begin()
    {
        if (!CanSelfUpdate || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        Error = null;
        Result = null;
        State = SelfUpdateState.Running;
        Notify();

        _ = Task.Run(async () =>
        {
            try
            {
                if (Mode == SelfUpdateMode.Docker)
                {
                    await BeginDockerAsync(CancellationToken.None);
                }
                else
                {
                    await BeginBareMetalAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    private void Fail(string reason)
    {
        Error = reason;
        State = SelfUpdateState.Failed;
        Log($"FAILED: {reason}");
        logger.Error("Self-update failed: {Reason}", reason);
        Notify();
    }

    private void Complete(string message)
    {
        Result = message;
        State = SelfUpdateState.Completed;
        Log(message);
        logger.Information("Self-update finished: {Message}", message);
        Notify();
    }

    // ── Log streaming ─────────────────────────────────────────────────────

    /// <summary>Snapshot of the streamed update log (newest last).</summary>
    public IReadOnlyList<string> GetLog()
    {
        lock (logLock)
        {
            return [.. logLines];
        }
    }

    private void Log(string line)
    {
        lock (logLock)
        {
            logLines.Add(line.Length > 500 ? line[..500] + "…" : line);
            if (logLines.Count > MaxLogLines)
            {
                logLines.RemoveAt(0);
            }
        }

        OnLogLine?.Invoke(line);
    }

    private void Notify() => OnStateChanged?.Invoke();

    // ── Process plumbing ──────────────────────────────────────────────────

    /// <summary>
    /// Runs a process, streaming stdout/stderr lines into the update log.
    /// Throws <see cref="TimeoutException"/> when the process exceeds
    /// <paramref name="timeout"/>.
    /// </summary>
    private async Task<int> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = CreateStartInfo(fileName, arguments);

        process.Start();

        var pumpOutput = PumpAsync(process.StandardOutput);
        var pumpError = PumpAsync(process.StandardError);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }

            throw cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(cancellationToken)
                : new TimeoutException($"'{fileName} {string.Join(' ', arguments)}' timed out after {timeout.TotalMinutes:0} min");
        }

        await Task.WhenAll(pumpOutput, pumpError);
        return process.ExitCode;
    }

    private static async Task<string> CaptureProcessOutputAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = CreateStartInfo(fileName, arguments);

        process.Start();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        string output;
        try
        {
            output = await process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }

            throw cancellationToken.IsCancellationRequested
                ? new OperationCanceledException(cancellationToken)
                : new TimeoutException($"'{fileName} {string.Join(' ', arguments)}' timed out");
        }

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(CancellationToken.None);
            throw new InvalidOperationException($"'{fileName} {string.Join(' ', arguments)}' failed: {error.Trim()}");
        }

        return output;
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> arguments)
    {
        // No explicit environment: the child inherits this process's env, which
        // carries BRIDGE_PORT / APACE_CHANNEL into the compose interpolation
        // (on top of the generated --env-file).
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                Log(line);
            }
        }
    }

    private static string Describe(Dictionary<string, string> ports)
        => string.Join(", ", ports.OrderBy(p => p.Key).Select(p => $"{p.Key}→{p.Value}"));

    private static string? ResolveDockerCli()
    {
        var candidates = new List<string>();

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (pathVariable is not null)
        {
            candidates.AddRange(pathVariable.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(dir => Path.Combine(dir, "docker")));
        }

        candidates.Add("/usr/local/bin/docker");
        candidates.Add("/usr/bin/docker");
        candidates.Add("/snap/bin/docker");

        return candidates.FirstOrDefault(File.Exists);
    }
}

/// <summary>Small JsonElement helpers so a missing property reads as null instead of throwing.</summary>
file static class JsonElementExtensions
{
    /// <summary>Walks a property path, returning a default (Undefined) element when anything along the way is missing.</summary>
    public static JsonElement GetElement(this JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            current = current.TryGetProperty(name, out var property) ? property : default;
        }

        return current;
    }

    /// <summary>Walks a property path and returns the string at its end, or null when anything along the way is missing.</summary>
    public static string? GetString(this JsonElement element, params string[] path)
    {
        var current = element.GetElement(path);
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
