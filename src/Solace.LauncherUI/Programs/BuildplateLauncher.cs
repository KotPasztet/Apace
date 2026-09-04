using Serilog;
using Solace.Common.Utils;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ILogger = Serilog.ILogger;

namespace Solace.LauncherUI.Programs;

internal static class BuildplateLauncher
{
    public static readonly string ExeName = "BuildplateLauncher" + (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "");
    public const string DispName = "Buildplate launcher";

    public const string ServerJarName = "fabric-server-mc.1.20.4-loader.0.15.10-launcher.1.0.1.jar";

    public static readonly Version MinimumFountainBridgeVersion = new Version(0, 0, 1);
    public static readonly Version MinimumBuildplateConnectorPluginVersion = new Version(0, 0, 1);

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

        var serverJarsDir = Path.GetFullPath(Path.Combine(Program.StaticDataDir, "server_jars"));

        if (!File.TryFindCompatibleFile(serverJarsDir, MinimumBuildplateConnectorPluginVersion, "buildplate-connector-plugin-{{version}}-SNAPSHOT-jar-with-dependencies.jar", out var connectorPluginPath))
        {
            logger.Error("Could not find buildplate connector plugin jar, expected '{Path}', with minimum version {Version}", Path.Combine(serverJarsDir, "buildplate-connector-plugin-{{version}}-SNAPSHOT-jar-with-dependencies.jar"), MinimumBuildplateConnectorPluginVersion);
            return null;
        }

        if (!File.TryFindCompatibleFile(serverJarsDir, MinimumFountainBridgeVersion, "fountain-{{version}}-SNAPSHOT-jar-with-dependencies.jar", out var fountainBridgePath))
        {
            logger.Error("Could not find fountain bridge jar, expected '{Path}', with minimum version {Version}", Path.Combine(serverJarsDir, "fountain-{{version}}-SNAPSHOT-jar-with-dependencies.jar"), MinimumFountainBridgeVersion);
            return null;
        }
        
        MigrateLegacyPersistentFabricDir(logger);

        var arguments = new List<string>(10)
        {
            $"--eventbus=localhost:{settings.EventBusPort}",
            $"--publicAddress={settings.IPv4}",
            $"--bridgePort={settings.BridgePort}",
            $"--bridgeJar={fountainBridgePath}",
            $"--serverTemplateDir={Path.GetFullPath(Path.Combine(Program.StaticDataDir, "server_template_dir"))}",
            $"--fabricJarName={ServerJarName}",
            $"--connectorPluginJar={connectorPluginPath}",
            $"--persistentFabricDir={Program.PersistentFabricDir}",
            $"--dir={Program.StaticDataDir}",
            $"--logger-url={Program.LoggerAddress}",
        };

        return Process.Start(new ProcessStartInfo(Path.GetFullPath(Path.Combine(Program.ProgramsDir, ExeName)), arguments)
        {
            WorkingDirectory = Path.GetFullPath(Program.ProgramsDir),
            CreateNoWindow = false,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Copies the persistent server's <c>server.properties</c> from the old
    /// default location inside the components directory to the new one, so hand
    /// edited values survive the move. Runs before the launcher is started,
    /// because it writes the file itself on the first start of a fresh working
    /// directory. Skipped when the source file does not exist (fresh install or
    /// the old directory is already gone) or when the target already has the
    /// file (the migration already ran, or the target directory was set up
    /// independently of the components directory).
    /// </summary>
    private static void MigrateLegacyPersistentFabricDir(ILogger logger)
    {
        try
        {
            // File.Exists (not FileInfo.Exists) is used, because FileInfo caches
            // the result of its first Exists check (same reasoning as in the
            // buildplate launcher's server.properties handling).
            string legacyPropertiesPath = Path.Combine(Program.ProgramsDir, "persistent_fabric", "server.properties");
            string propertiesPath = Path.Combine(Program.PersistentFabricDir, "server.properties");

            if (!File.Exists(legacyPropertiesPath) || File.Exists(propertiesPath))
            {
                return;
            }

            Directory.CreateDirectory(Program.PersistentFabricDir);
            File.Copy(legacyPropertiesPath, propertiesPath, overwrite: false);
            logger.Information($"Migrated the persistent Fabric server server.properties from {legacyPropertiesPath} to {propertiesPath}");
        }
        catch (Exception exception)
        {
            // The launcher generates the file when it is missing, so a failed
            // migration only means hand edited values are lost, not a broken
            // server.
            logger.Warning(exception, "Could not migrate the persistent Fabric server server.properties from the components directory");
        }
    }
}
