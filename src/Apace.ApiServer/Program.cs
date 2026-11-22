using CommandLine;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;
using System.ComponentModel;
using System.Diagnostics;
using Uma.Uuid;
using Apace.ApiServer.Utils;
using Apace.BuildplateImporter;
using Apace.Common;
using Apace.Common.Utils;
using Apace.DB;
using Apace.EventBus.Client;
using Apace.ObjectStore.Client;
using Apace.StaticData;
using SData = Apace.StaticData.StaticData;

namespace Apace.ApiServer;

public static class Program
{
    // initialized in main
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    internal static Config config;

    internal static EarthDB DB;
    internal static SData staticData;
    internal static EventBusClient eventBus;
    private static string objectStoreClientConnectionString;
    internal static TappablesManager tappablesManager;
    internal static BuildplateInstancesManager buildplateInstancesManager;
    internal static Importer importer;
    internal static int PlayableScale { get; private set; } = 4;
    internal static bool LocalLoginOnly { get; private set; }
    internal static bool RequireMojangAssets { get; private set; }

    /// <summary>
    /// Persistent directory with the additional Mojang assets (default
    /// ../data/mojang-assets — the docker data volume). The CDN controllers
    /// serve from here first and fall back to staticdata.
    /// </summary>
    internal static string MojangAssetsDir { get; private set; } = "";

#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    private sealed class Options
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    {
        [Option("port", Default = 80, Required = false, HelpText = "Port to listen on")]
        public int HttpPort { get; set; }

        [Option("earth-db", Default = "./earth.db", Required = false, HelpText = "Earth database connection string")]
        public string EarthDatabaseConnectionString { get; set; }

        [Option("live-db", Default = "./live.db", Required = false, HelpText = "Live database connection string")]
        public string LiveDatabaseConnectionString { get; set; }

        [Option("dir", Default = "./staticdata", Required = false, HelpText = "Static data path")]
        public string StaticDataPath { get; set; }

        [Option("eventbus", Default = "localhost:5532", Required = false, HelpText = "Event bus address")]
        public string EventBusConnectionString { get; set; }

        [Option("objectstore", Default = "localhost:5396", Required = false, HelpText = "Object storage address")]
        public string ObjectStoreConnectionString { get; set; }

        [Option("logger-url", Default = null, Required = false, HelpText = "Url to send logs to")]
        public string? LoggerUrl { get; set; }

        [Option("playable-scale", Default = 4, Required = false, HelpText = "AR scale for playable buildplates and player adventures, in blocks per meter")]
        public int PlayableScale { get; set; }

        [Option("local-login-only", Default = false, Required = false, HelpText = "Whether to only allow local accounts, or also allow microsoft accounts")]
        public bool LocalLoginOnly { get; set; }

        [Option("require-mojang-assets", Default = false, Required = false, HelpText = "Block startup while the additional Mojang assets are incomplete in the Mojang assets dir")]
        public bool RequireMojangAssets { get; set; }

        [Option("mojang-assets-dir", Default = null, Required = false, HelpText = "Persistent directory holding the additional Mojang assets (default ../data/mojang-assets)")]
        public string? MojangAssetsDir { get; set; }
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

    public static async Task<int> Main(string[] args)
    {
        TypeDescriptor.AddAttributes(typeof(Uuid), new TypeConverterAttribute(typeof(StringToUuidConv)));

        Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;

        /*var log = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("logs/api_server/log.txt", rollingInterval: RollingInterval.Day, rollOnFileSizeLimit: true, fileSizeLimitBytes: 8338607, outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Apace.ApiServer.Authentication", LogEventLevel.Warning)
            .CreateLogger();*/

        if (!Debugger.IsAttached)
        {
            AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
            {
                Directory.CreateDirectory("logs/api_server");
                File.WriteAllText("logs/api_server/_crash.txt", e.ExceptionObject.ToString());

                Log.Fatal($"Unhandled exception: {e.ExceptionObject}");
                Log.CloseAndFlush();
                Environment.Exit(1);
            };
        }

        ParserResult<Options> res = Parser.Default.ParseArguments<Options>(args);

        Options options;
        if (res is Parsed<Options> parsed)
        {
            options = parsed.Value;
        }
        else if (res is NotParsed<Options> notParsed)
        {
            if (res.Errors.Any(error => error is HelpRequestedError))
            {
                return 0;
            }
            else if (res.Errors.Any(error => error is VersionRequestedError))
            {
                return 0;
            }
            else
            {
                return 1;
            }
        }
        else
        {
            return 1;
        }

        PlayableScale = Math.Clamp(options.PlayableScale, 1, 32);
        LocalLoginOnly = options.LocalLoginOnly;
        RequireMojangAssets = options.RequireMojangAssets;
        MojangAssetsDir = Path.GetFullPath(options.MojangAssetsDir ?? Path.Combine("..", "data", "mojang-assets"));

        var loggerConfig = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("logs/api_server/log.txt", rollingInterval: RollingInterval.Day, rollOnFileSizeLimit: true, fileSizeLimitBytes: 8338607, outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .Enrich.WithProperty("ComponentName", "ApiServer");

        if (!string.IsNullOrWhiteSpace(options.LoggerUrl))
        {
            loggerConfig.WriteTo.Http(options.LoggerUrl, 10 * 1024 * 1024);
        }

        loggerConfig
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Information)
            .MinimumLevel.Override("Apace.ApiServer.Authentication", LogEventLevel.Information);
        var log = loggerConfig.CreateLogger();

        Log.Logger = log;

        if (options.LocalLoginOnly)
        {
            Log.Information("Local account only login enabled, Microsoft accounts will not work");
        }
        else
        {
            // informational only — the default login mode signs clients in through the login
            // page (the fake login.live.com flow); this is NOT an error
            Log.Information("Login mode: Microsoft-style account login (clients sign in through the login page)");
        }

        // Mojang asset gate (direct start path): when the panel flagged the gate
        // on, the server refuses to boot without the additional Mojang assets in
        // the persistent assets dir — the panel must accept and download them
        // first. Without the flag the gate is disabled and the check is a warn.
        {
            string? manifestPath = Utils.MojangAssets.ResolveManifestPath(MojangAssetsDir, Log.Logger);
            int? missing = manifestPath is null ? null : Utils.MojangAssets.CountMissing(MojangAssetsDir, manifestPath, Log.Logger);

            if (RequireMojangAssets)
            {
                if (missing is null)
                {
                    Log.Fatal("Additional Mojang assets missing — run the panel to accept and download; server startup blocked");
                    Log.CloseAndFlush();
                    return 1;
                }

                if (missing > 0)
                {
                    Log.Fatal("Additional Mojang assets missing — run the panel to accept and download; server startup blocked ({Count} file(s) incomplete in {Dir}, manifest {Manifest})", missing, MojangAssetsDir, manifestPath);
                    Log.CloseAndFlush();
                    return 1;
                }

                Log.Information("Additional Mojang assets complete in {Dir}", MojangAssetsDir);
            }
            else if (missing is not null && missing > 0)
            {
                Log.Warning("Additional Mojang assets incomplete ({Count} file(s) in {Dir}) — the asset gate is disabled, serving may fall back to staticdata", missing, MojangAssetsDir);
            }
        }

        Log.Information("Loading configuration");
        bool secretsReused = false;
        string dataConfigPath = string.Empty;
        try
        {
            // The login secrets must survive updates: when they rotate, every client holding
            // a cached ticket (valid up to 7 days, never re-requested while unexpired) is
            // locked out in a 401 loop on user/authenticate.
            //
            // Primary location is the data directory (../data): in docker it is the
            // persistent data volume (/app/data), outside docker it is the installation's
            // data folder — both survive image updates and file replacements.
            //
            // The previous location — the current directory — has a failure mode in docker:
            // docker-compose bind-mounts api_config.json as a single FILE, and when the host
            // file is missing Docker creates a DIRECTORY at that path instead, so the server
            // can neither read nor write it and regenerated random secrets on every boot.
            // It is still read for backward compatibility (and its contents are migrated to
            // the data directory so a later compose update cannot strand them).
            const string configFileName = "api_config.json";
            dataConfigPath = Path.Combine("..", "data", configFileName);
            string legacyConfigPath = configFileName;

            if (File.Exists(dataConfigPath))
            {
                try
                {
                    config = Json.Deserialize<Config>(File.ReadAllText(dataConfigPath));
                }
                catch (System.Text.Json.JsonException ex)
                {
                    Log.Warning($"Configuration file {dataConfigPath} is invalid: {ex.Message}");
                    config = null;
                }
            }

            if (config is null && File.Exists(legacyConfigPath))
            {
                try
                {
                    config = Json.Deserialize<Config>(File.ReadAllText(legacyConfigPath));

                    // migrate to the data directory so the secrets survive a docker compose
                    // refresh that drops the legacy file bind-mount
                    if (config is not null)
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dataConfigPath))!);
                            File.WriteAllText(dataConfigPath, Json.SerializeIndented(config));
                            Log.Information($"Migrated login secrets from {Path.GetFullPath(legacyConfigPath)} to {Path.GetFullPath(dataConfigPath)}");
                        }
                        catch (Exception migrateEx)
                        {
                            Log.Warning($"Could not migrate the configuration to {Path.GetFullPath(dataConfigPath)}: {migrateEx.Message}");
                        }
                    }
                }
                catch (System.Text.Json.JsonException ex)
                {
                    Log.Warning($"Configuration file {legacyConfigPath} is invalid: {ex.Message}");
                    config = null;
                }
            }

            // loaded from either location ⇒ the secrets (and every client session) survive this boot
            secretsReused = config is not null;
            if (config is null)
            {
                // missing, empty or unparseable configuration gets fresh random secrets (the old hardcoded defaults
                // were the public upstream Solace secrets, so anyone could forge tokens for servers using them)
                config = Config.Default;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dataConfigPath))!);
                    File.WriteAllText(dataConfigPath, Json.SerializeIndented(config));
                }
                catch (Exception writeEx)
                {
                    Log.Warning($"Could not persist the generated configuration to {Path.GetFullPath(dataConfigPath)}: {writeEx.Message}");
                    try
                    {
                        File.WriteAllText(legacyConfigPath, Json.SerializeIndented(config));
                    }
                    catch (Exception legacyWriteEx)
                    {
                        Log.Error($"Could not persist the generated configuration to {Path.GetFullPath(legacyConfigPath)} either: {legacyWriteEx.Message} " +
                                  $"(login secrets will rotate on every restart and clients will be locked out in a 401 loop — fix the data directory permissions)");
                    }
                }

                Log.Warning($"Configuration file not found or invalid, created with random secrets: {Path.GetFullPath(dataConfigPath)} " +
                            $"(tokens issued before are no longer valid, clients will be sent through the login flow again)");
            }
        }
        catch (Exception ex)
        {
            Log.Fatal($"Failed to load configuration: {ex}");
            Log.CloseAndFlush();
            return 1;
        }

        Log.Information("Loaded configuration");
        Log.Information(secretsReused
            ? $"Login secrets reused from {Path.GetFullPath(dataConfigPath)} — future redeploys keep all client sessions valid"
            : "Login secrets generated fresh — clients holding tickets from before this boot must sign in once through the login page");

        Log.Information("Connecting to earth database");
        try
        {
            DB = EarthDB.Open(options.EarthDatabaseConnectionString);
        }
        catch (EarthDB.DatabaseException ex)
        {
            Log.Fatal($"Could not connect to database: {ex}");
            Log.CloseAndFlush();
            return 1;
        }

        Log.Information("Connected to earth database");

        Log.Information("Connecting to event bus");
        try
        {
            eventBus = await EventBusClient.ConnectAsync(options.EventBusConnectionString);
        }
        catch (EventBusClientException ex)
        {
            Log.Fatal($"Could not connect to event bus: {ex}");
            Log.CloseAndFlush();
            return 1;
        }

        Log.Information("Connected to event bus");
        Log.Information("Connecting to object storage");
        ObjectStoreClient objectStore;
        try
        {
            objectStore = await ObjectStoreClient.ConnectAsync(options.ObjectStoreConnectionString);
            objectStoreClientConnectionString = options.ObjectStoreConnectionString;
        }
        catch (ObjectStoreClientException ex)
        {
            Log.Fatal($"Could not connect to object storage: {ex}");
            Log.CloseAndFlush();
            return 1;
        }

        Log.Information("Connected to object storage");

        Log.Information("Loading static data");
        try
        {
            staticData = new SData(options.StaticDataPath);
        }
        catch (StaticDataException staticDataException)
        {
            Log.Fatal($"Failed to load static data: {staticDataException}");
            Log.CloseAndFlush();
            return 1;
        }

        Log.Information("Loaded static data");

        Log.Information("Importing shop buildplates");

        EarthDB.ObjectResults? currentShopBuildplates = null;
        try
        {
            currentShopBuildplates = await new EarthDB.ObjectQuery(true)
                .GetBuildplates(staticData.Buildplates.ShopBuildplates.Select(buildplate => buildplate.Id))
                .ExecuteAsync(DB);
        }
        catch (EarthDB.DatabaseException ex)
        {
            Log.Error($"Failed to get current shop buildplates: {ex}");
        }

        importer = new Importer(DB, eventBus, objectStore, Log.Logger);
        if (currentShopBuildplates is not null)
        {
            foreach (var buidplate in staticData.Buildplates.ShopBuildplates)
            {
                if (currentShopBuildplates.GetBuildplate(buidplate.Id) is not null)
                {
                    Log.Debug($"Shop buildplate {buidplate.Id} already exists");
                    continue;
                }

                try
                {
                    Log.Information($"Importing shop buildplate {buidplate.Id}");

                    string name = buidplate.Id;
                    if (Guid.TryParse(buidplate.Id, out var buidplateGuid))
                    {
                        var bpPlayfabItem = staticData.Playfab.Items.Values.FirstOrDefault(item => item.Data is Playfab.Item.BuildplateData bpData && bpData.Id == buidplateGuid);
                        if (bpPlayfabItem is not null)
                        {
                            name = bpPlayfabItem.Title;
                        }
                    }

                    using (var buidplateData = buidplate.OpenRead())
                    {
                        await importer.ImportTemplateAsync(buidplate.Id, $"[SHOP] {name}", buidplateData);
                    }
                }
                catch (Exception ex)
                {
                    Log.Fatal($"Failed to import shop buidplate {buidplate.Id}: {ex}");
                    Log.CloseAndFlush();
                    return 1;
                }
            }
        }

        Log.Information("Imported shop buidplates");

        tappablesManager = await TappablesManager.CreateAsync(eventBus);
        buildplateInstancesManager = await BuildplateInstancesManager.CreateAsync(eventBus);

        BuildplateInstanceRequestHandler.Start(DB, eventBus, objectStore, staticData.Catalog);

        var host = CreateHostBuilder(args, options.HttpPort, options.LiveDatabaseConnectionString).Build();

        Log.Information("Updating live db");
        using (var scope = host.Services.CreateScope())
        {
            var liveDb = scope.ServiceProvider.GetRequiredService<LiveDbContext>();
            liveDb.Database.Migrate();
        }

        Log.Information("Updated live db");

        host.Run();

        return 0;
    }

    public static IHostBuilder CreateHostBuilder(string[] args, int httpPort, string liveDbConnectionString)
        => Host.CreateDefaultBuilder(args)
            .UseSerilog()
            .ConfigureAppConfiguration((hostingContext, config) =>
            {
                config.AddInMemoryCollection([
                    new("ConnectionStrings:LiveDBConnection", "Data Source=" + liveDbConnectionString)
                ]);
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>();
                webBuilder.UseUrls($"http://*:{httpPort}/");
            });

    public static async Task<ObjectStoreClient> GetObjectStoreClient()
        => await ObjectStoreClient.ConnectAsync(objectStoreClientConnectionString);
}
