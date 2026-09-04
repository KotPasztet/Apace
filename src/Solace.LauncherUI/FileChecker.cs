using Serilog;
using System.Diagnostics;
using System.Reflection;
using Solace.Common;
using Solace.Common.Utils;
using Solace.LauncherUI.Programs;
using ILogger = Serilog.ILogger;

namespace Solace.LauncherUI;

internal static class FileChecker
{
    private static readonly HttpClient httpClient = new();

    // EULA acceptance mechanism — exposed to panel UI
    private static TaskCompletionSource? _eulaTcs;
    private static string? _eulaPath;
    public static bool EulaPending => _eulaTcs is not null && !_eulaTcs.Task.IsCompleted;
    public static string? EulaPath => _eulaPath;

    public static void AcceptEula()
    {
        if (_eulaPath is not null && File.Exists(_eulaPath))
        {
            File.WriteAllText(_eulaPath, "eula=true");
            _eulaTcs?.TrySetResult();
        }
    }

    // Resource pack prompt mechanism — exposed to panel UI.
    //
    // Unlike the EULA gate this never blocks startup: vanilla.zip is optional
    // (game clients ship the pack embedded, this copy only serves overrides),
    // so CheckAsync keeps its non-fatal semantics and merely arms the prompt.
    //
    // State machine:
    //  - CheckAsync arms the prompt when the file is missing or undersized
    //    (and re-arms a prompt dismissed earlier in this panel session).
    //  - ResourcePackPending re-stats the file on every read, so the panel's
    //    2 s poll clears the alert as soon as the file becomes valid — whether
    //    it appeared by download or manual placement.
    //  - DismissResourcePackPrompt() silences it for the current panel session
    //    (static flag, so it re-arms on the next panel restart) or until the
    //    next CheckAsync that still finds the file invalid.
    private static bool _resourcePackIssueDetected;
    private static string? _resourcePackPath;
    private static bool _resourcePackDismissed;

    public static bool ResourcePackPending => _resourcePackIssueDetected && !_resourcePackDismissed && !ResourcePackFileIsValid();
    public static string? ResourcePackPath => _resourcePackPath;

    public static void DismissResourcePackPrompt() => _resourcePackDismissed = true;

    private static void FlagResourcePackProblem(string path)
    {
        _resourcePackIssueDetected = true;
        _resourcePackPath = path;
        // A fresh detection re-arms a prompt dismissed earlier in this session.
        _resourcePackDismissed = false;
    }

    private const string ResourcePackId = "dba38e59-091a-4826-b76a-a08d7de5a9e2-1301b0c257a311678123b9e7325d0d6c61db3c35";
    private const long MinResourcePackSize = 100_000_000; // ~131MB expected (131885348B)

    private static string GetResourcePackPath()
        => Path.GetFullPath(Path.Combine(Program.StaticDataDir, "resourcepacks", "vanilla.zip"));

    private static bool ResourcePackFileIsValid()
    {
        var info = new FileInfo(GetResourcePackPath());
        return info.Exists && info.Length >= MinResourcePackSize;
    }

    private static readonly string[] expectedStaticFiles =
    [
        "catalog/itemEfficiencyCategories.json",
        "catalog/itemJournalGroups.json",
        "catalog/items.json",
        "catalog/nfc.json",
        "catalog/recipes.json",
        "catalog/recipes.json",
        "tile_renderer/tagMap1.json",
        "tile_renderer/tagMap2.json",
    ];

    private static readonly (string Template, Version MinimumVersion)[] expectedVersionedStaticFiles =
    [
        ("server_jars/buildplate-connector-plugin-{{version}}-SNAPSHOT-jar-with-dependencies.jar", BuildplateLauncher.MinimumBuildplateConnectorPluginVersion),
        ("server_jars/fountain-{{version}}-SNAPSHOT-jar-with-dependencies.jar", BuildplateLauncher.MinimumFountainBridgeVersion),
        ("server_template_dir/mods/fountain-{{version}}.jar", new Version(0, 0, 1)),
        ("server_template_dir/mods/vienna-{{version}}.jar", new Version(0, 0, 1)),
    ];

    private static readonly string[] expectedStaticDirectories = [
        "catalog",
        "encounters",
        "levels",
        "resourcepacks",
        "server_jars",
        "server_template_dir",
        "server_template_dir/mods",
        "tappables",
        "tile_renderer",
    ];

    static FileChecker()
    {
        bool added = httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"BitcoderCZ/Solace/{Assembly.GetExecutingAssembly().GetName().Version}");
        Debug.Assert(added);
    }

    public static async Task<bool> CheckAsync(Settings settings, bool checkImporter, ILogger logger, CancellationToken cancellationToken)
    {
        if (settings.SkipFileChecks is not true)
        {
            logger.Information("Validating files");
        }
        else
        {
            logger.Warning("Skipped file validation, you can turn it back on in 'Configure/Skip file validation before starting'");
            return true;
        }

        bool error = false;
        if (!EventBusServer.Check(settings, logger) ||
            !ObjectStoreServer.Check(settings, logger) ||
            !ApiServer.Check(settings, logger) ||
            !BuildplateLauncher.Check(settings, logger) ||
            !TappablesGenerator.Check(settings, logger) ||
            !TileRenderer.Check(settings, logger))
        {
            error = true;
        }

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var dir in expectedStaticDirectories)
        {
            var fullDir = Path.GetFullPath(Path.Combine(Program.StaticDataDir, dir));

            if (!Directory.Exists(fullDir))
            {
                Directory.CreateDirectory(fullDir);
                logger.Warning($"Static data directory '{fullDir}' did not exist, created");
            }
        }

        foreach (var file in expectedStaticFiles)
        {
            var fullFile = Path.GetFullPath(Path.Combine(Program.StaticDataDir, file));

            if (!File.Exists(fullFile))
            {
                logger.Error($"Static data file '{fullFile}' does not exist");
                error = true;
            }
        }

        foreach (var (template, minimumVersion) in expectedVersionedStaticFiles)
        {
            var fileName = Path.GetFileName(template);
            var directory = Path.GetFullPath(Path.Combine(Program.StaticDataDir, Path.GetDirectoryName(template)!));

            if (!File.TryFindCompatibleFile(directory, minimumVersion, fileName, out var path))
            {
                // server_jars are critical (needed by BuildplateLauncher)
                // server_template_dir/mods are Fabric plugins — non-critical at startup
                bool isCritical = directory.Contains("server_jars");

                if (isCritical)
                {
                    logger.Error("Static data file '{Path}' does not exist, is outdated, or unsupported. Minimum version is {MinimumVersion}", Path.GetFullPath(Path.Combine(Program.StaticDataDir, template)), minimumVersion);
                    error = true;
                }
                else
                {
                    logger.Warning("Optional static file '{Path}' not found — will be needed for buildplate functionality", Path.GetFullPath(Path.Combine(Program.StaticDataDir, template)));
                }
            }
            else
            {
                logger.Debug("Versioned static file found '{Path}'", path);
            }
        }

        logger.Debug("All static files exist");

        var resourcePackPath = GetResourcePackPath();
        var resourcePack = new FileInfo(resourcePackPath);
        if (!resourcePack.Exists)
        {
            logger.Error($"Resourcepack file '{resourcePackPath}' does not exist");
            FlagResourcePackProblem(resourcePackPath);
        }
        else if (resourcePack.Length < MinResourcePackSize)
        {
            logger.Error($"Resourcepack file '{resourcePackPath}' is invalid, expected size: 131885348B, actual size: {resourcePack.Length}B");
            FlagResourcePackProblem(resourcePackPath);
        }

        if (_resourcePackIssueDetected)
        {
            // Informational only — the pack is optional, so startup continues.
            logger.Information("Resource pack is not downloaded automatically — use the panel prompt to download it or place the file manually");
        }

        if (checkImporter)
        {
            // todo:
            // if (!BuildplateImporter.Check(settings, logger))
            // {
            //     error = true;
            // }
        }
        else
        {
            if (!Directory.EnumerateFiles(Path.Combine(Program.StaticDataDir, "server_template_dir", "mods")).Any(path => Path.GetFileName(path).StartsWith("fabric-api", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
            {
                logger.Warning("Fabric api mod not found, downloading");

                var response = await httpClient.GetAsync("https://cdn.modrinth.com/data/P7dR8mSH/versions/xklQBMta/fabric-api-0.97.0%2B1.20.4.jar", cancellationToken);
                using (var fs = File.OpenWriteNew(Path.Combine(Program.StaticDataDir, "server_template_dir", "mods", "fabric-api-0.97.0+1.20.4.jar")))
                {
                    await response.Content.CopyToAsync(fs, cancellationToken);
                }

                logger.Information("Downloaded fabric api");
            }

            if (!File.Exists(Path.Combine(Program.StaticDataDir, "server_template_dir", BuildplateLauncher.ServerJarName)))
            {
                logger.Warning("Fabric server not found, downloading");

                var response = await httpClient.GetAsync("https://meta.fabricmc.net/v2/versions/loader/1.20.4/0.15.10/1.0.1/server/jar", cancellationToken);
                using (var fs = File.OpenWriteNew(Path.Combine(Program.StaticDataDir, "server_template_dir", BuildplateLauncher.ServerJarName)))
                {
                    await response.Content.CopyToAsync(fs, cancellationToken);
                }

                logger.Information("Downloaded fabric server");
            }

            string eulaPath = Path.GetFullPath(Path.Combine(Program.StaticDataDir, "server_template_dir", "eula.txt"));
            if (!File.Exists(eulaPath))
            {
                logger.Information("Detected that server was not setup, running");

                string javaExe = JavaLocator.Locate(logger);

                bool useShellExecute = false;

                using var serverProcess = new ConsoleProcess(javaExe, useShellExecute, !useShellExecute);

                if (!useShellExecute)
                {
                    serverProcess.StandartTextReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            logger.Debug($"[server] {e.Data}");
                        }
                    };
                    serverProcess.ErrorTextReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            logger.Error($"[server] {e.Data}");
                        }
                    };
                }

                await serverProcess.ExecuteAsync(Path.GetFullPath(Path.Combine(Program.StaticDataDir, "server_template_dir")), ["-jar", BuildplateLauncher.ServerJarName, "-nogui"]);
                logger.Information("Server process started, waiting for exit");
                await serverProcess.Process.WaitForExitAsync(cancellationToken);

                int exitCode = serverProcess.Process.ExitCode;
                logger.Information($"Server process exited with exit code {exitCode}");
                if (exitCode != 0)
                {
                    error = true;
                }
            }

            if (File.Exists(eulaPath) && !(await File.ReadAllTextAsync(eulaPath, cancellationToken)).Contains("eula=true", StringComparison.OrdinalIgnoreCase))
            {
                logger.Information($"Server eula not accepted, use the panel button or open '{eulaPath}' and set 'eula=true'");
                logger.Information("Waiting for EULA acceptance...");

                _eulaPath = eulaPath;
                _eulaTcs = new TaskCompletionSource();

                while (!(await File.ReadAllTextAsync(eulaPath, cancellationToken)).Contains("eula=true", StringComparison.OrdinalIgnoreCase))
                {
                    var delay = Task.Delay(1000, cancellationToken);
                    await Task.WhenAny(delay, _eulaTcs.Task);
                    if (_eulaTcs.Task.IsCompleted)
                        break;
                }

                _eulaTcs = null;
                _eulaPath = null;

                logger.Information("Running server to download/generate rest of the files, close it after it starts up");

                string javaExe = JavaLocator.Locate(logger);

                bool useShellExecute = true;

                using var serverProcess = new ConsoleProcess(javaExe, useShellExecute, !useShellExecute);

                if (!useShellExecute)
                {
                    serverProcess.StandartTextReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            logger.Debug($"[server] {e.Data}");
                        }
                    };
                    serverProcess.ErrorTextReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data))
                        {
                            logger.Error($"[server] {e.Data}");
                        }
                    };
                }

                await serverProcess.ExecuteAsync(Path.GetFullPath(Path.Combine(Program.StaticDataDir, "server_template_dir")), ["-jar", BuildplateLauncher.ServerJarName, "-nogui"]);
                logger.Information("Server process started, waiting for exit");
                await serverProcess.Process.WaitForExitAsync(cancellationToken);

                int exitCode = serverProcess.Process.ExitCode;
                logger.Information($"Server process exited with exit code {exitCode}");
                if (exitCode != 0)
                {
                    error = true;
                }
            }
        }

        return !error;
    }

    /// <summary>
    /// Downloads vanilla.zip on demand (panel "Server Status" prompt).
    /// Not critical — server runs without it. Clients have the resourcepack
    /// embedded, this is only needed for overrides.
    /// Does not throw: failures are logged and the file is left absent/invalid,
    /// so callers should re-check <see cref="ResourcePackPending"/>.
    /// </summary>
    public static Task DownloadResourcePackAsync(IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        => DownloadResourcePackAsync(GetResourcePackPath(), Log.Logger, progress, cancellationToken);

    private static async Task DownloadResourcePackAsync(string destPath, ILogger logger, IProgress<float>? progress, CancellationToken cancellationToken)
    {
        var urls = new[]
        {
            $"https://cdn.mceserv.net/availableresourcepack/resourcepacks/{ResourcePackId}",
            $"https://web.archive.org/web/2/https://cdn.mceserv.net/availableresourcepack/resourcepacks/{ResourcePackId}",
        };

        foreach (var url in urls)
        {
            try
            {
                logger.Information($"Downloading resourcepack from {url}...");
                using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    logger.Warning($"Download failed from {url}: HTTP {(int)response.StatusCode}");
                    continue;
                }

                // Check Content-Length header before downloading the body
                if (response.Content.Headers.ContentLength is long contentLength && contentLength < MinResourcePackSize)
                {
                    logger.Warning($"Resourcepack at {url} is too small ({contentLength} bytes), skipping");
                    continue;
                }

                progress?.Report(0);

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                long? totalBytes = response.Content.Headers.ContentLength;
                await using var fs = File.OpenWriteNew(destPath);
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[81920];
                long downloadedBytes = 0;
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloadedBytes += bytesRead;
                    if (progress is not null && totalBytes > 0)
                    {
                        progress.Report((float)downloadedBytes / totalBytes.Value);
                    }
                }
                await fs.FlushAsync(cancellationToken);

                var fileInfo = new FileInfo(destPath);
                if (fileInfo.Length < MinResourcePackSize)
                {
                    logger.Warning($"Downloaded resourcepack is too small ({fileInfo.Length} bytes), deleting");
                    File.Delete(destPath);
                    continue;
                }

                logger.Information($"Resourcepack downloaded successfully ({fileInfo.Length} bytes) to '{destPath}'");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warning($"Download from {url} failed: {ex.Message}");
                // Clean up partial download
                try { File.Delete(destPath); } catch { /* ignore */ }
            }
        }

        logger.Warning("Could not download resourcepack. The server will work without it.");
        logger.Warning($"To add it manually, download from Internet Archive and place at: {destPath}");
        logger.Warning($"  Archive search: https://web.archive.org/web/*/https://cdn.mceserv.net/availableresourcepack/resourcepacks/{ResourcePackId}");
    }
}
