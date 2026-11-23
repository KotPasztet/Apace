using Serilog;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ILogger = Serilog.ILogger;

namespace Apace.LauncherUI.Programs;

internal static class ApiServer
{
    public static readonly string ExeName = "ApiServer" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "");
    public const string DispName = "Api server";

#pragma warning disable IDE0060 // Remove unused parameter
    public static bool Check(Settings settings, ILogger logger)
#pragma warning restore IDE0060 // Remove unused parameter
    {
        string exePath = Path.GetFullPath(Path.Combine(Program.ProgramsDir, ExeName));
        if (!File.Exists(exePath))
        {
            logger.Error($"{DispName} exe doesn't exits: {exePath}");
            return false;
        }

        return true;
    }

    public static Process? Run(Settings settings, ILogger logger)
    {
        logger.Debug($"Running {DispName}");

        var arguments = new List<string>(8)
        {
            $"--port={settings.ApiPort}",
            $"--earth-db={settings.EarthDatabaseConnectionString}",
            $"--live-db={settings.LiveDatabaseConnectionString}",
            $"--eventbus=localhost:{settings.EventBusPort}",
            $"--objectstore=localhost:{settings.ObjectStorePort}",
            $"--logger-url={Program.LoggerAddress}",
            $"--dir={Program.StaticDataDir}",
            $"--playable-scale={settings.PlayableScale}",
        };

        if (settings.OnlyAllowLocalLogin is true)
        {
            arguments.Add($"--local-login-only");
        }

        // The assets dir is always passed so the CDN controllers can serve the
        // persona binaries, thumbnails and skin packs from the data volume.
        // Startup never depends on these assets — their download sources are
        // dead and the live marketplace sync replaces the legacy pieces.
        arguments.Add($"--mojang-assets-dir={Apace.LauncherUI.Services.MojangAssetPaths.MojangAssetsDir}");

        // Live persona marketplace: serve the panel-synced live catalog (unlock-all
        // offer, lazy binary probe) instead of the dead legacy 1P persona items.
        if (settings.LivePersonaCatalog is not false)
        {
            arguments.Add("--live-persona-catalog");
        }

        return Process.Start(new ProcessStartInfo(Path.GetFullPath(Path.Combine(Program.ProgramsDir, ExeName)), arguments)
        {
            WorkingDirectory = Path.GetFullPath(Program.ProgramsDir),
            CreateNoWindow = false,
            UseShellExecute = true,
        });
    }
}
