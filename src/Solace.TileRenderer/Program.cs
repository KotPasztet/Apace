using CommandLine;
using Npgsql;
using Serilog;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Solace.EventBus.Client;
using Solace.StaticData;
using System.Globalization;

namespace Solace.TileRenderer;

internal static class Program
{
    private sealed class Options
    {
        [Option("maptiler_key", HelpText = "Api key for maptiler.com", SetName = "Data source")]
        public string? MapTilerApiKey { get; set; }

        [Option("tileDB", HelpText = "Connection string to a postgresql database with tile data, for example 'Host=myserver;Username=mylogin;Password=mypass;Database=mydatabase'", SetName = "Data source")]
        public string? TileDatabaseConnectionString { get; set; }

        [Option("eventbus", Default = "localhost:5532", Required = false, HelpText = "Event bus address")]
        public string? EventBusConnectionString { get; set; }

        [Option("dir", Default = "./staticdata", Required = false, HelpText = "Static data path")]
        public string? StaticDataPath { get; set; }

        [Option("logger-url", Default = null, Required = false, HelpText = "Url to send logs to")]
        public string? LoggerUrl { get; set; }
    }

    private static async Task<int> Main(string[] args)
    {
        if (!Debugger.IsAttached)
        {
            AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
            {
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
        else
        {
            return res is NotParsed<Options> notParsed
                ? res.Errors.Any(error => error is HelpRequestedError)
                    ? 0
                    : res.Errors.Any(error => error is VersionRequestedError)
                    ? 0
                    : 1
                : 1;
        }

        var loggerConfig = new LoggerConfiguration()
                 .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
                 .WriteTo.File("logs/tile_renderer/log.txt", rollingInterval: RollingInterval.Day, rollOnFileSizeLimit: true, fileSizeLimitBytes: 8338607, outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}", formatProvider: CultureInfo.InvariantCulture)
                 .Enrich.WithProperty("ComponentName", "TileRenderer");

        if (!string.IsNullOrWhiteSpace(options.LoggerUrl))
        {
            loggerConfig.WriteTo.Http(options.LoggerUrl, 10 * 1024 * 1024);
        }

        loggerConfig.MinimumLevel.Debug();
        var log = loggerConfig.CreateLogger();

        Log.Logger = log;

        if (string.IsNullOrEmpty(options.MapTilerApiKey) && string.IsNullOrEmpty(options.TileDatabaseConnectionString))
        {
            Log.Fatal("No data source provided, either maptiler_key or tileDB must be specified.");
            Log.CloseAndFlush();
            return 1;
        }

        ITileDataSource tileDataSource;
        if (options.MapTilerApiKey is not null)
        {
            // A transient maptiler outage (unreachable api, timeouts, 5xx, rate
            // limiting) must not take the renderer down: the data source fails
            // per request at runtime, so tiles simply stop rendering until the
            // api is reachable again. Only a definitive rejection (a 4xx status
            // - an invalid key) is fatal.
            const int verifyAttempts = 3;
            const int verifyTimeoutSeconds = 10;
            const int verifyRetryDelaySeconds = 3;

            Log.Information("Verifying maptiler api key");

            var httpClient = new HttpClient();
            HttpResponseMessage? response = null;
            Exception? connectError = null;
            for (int attempt = 1; attempt <= verifyAttempts; attempt++)
            {
                using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(verifyTimeoutSeconds));
                try
                {
                    response = await httpClient.GetAsync($"https://api.maptiler.com/tiles/v3/tiles.json?key={options.MapTilerApiKey}", timeoutSource.Token);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    connectError = ex;
                    Log.Warning($"Could not connect to maptiler api (attempt {attempt}/{verifyAttempts}): {ex.Message}");
                }

                if (attempt < verifyAttempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(verifyRetryDelaySeconds));
                }
            }

            int maxZoom = 15;
            if (response is null)
            {
                Log.Warning($"Could not reach the maptiler api after {verifyAttempts} attempts, starting anyway (tiles will fail to render until it is reachable again): {connectError?.Message}");
            }
            else if (!response.IsSuccessStatusCode)
            {
                // 408/429 are transient, every other 4xx means the key is bad.
                bool keyRejected = (int)response.StatusCode >= 400 && (int)response.StatusCode < 500
                    && response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);

                if (keyRejected)
                {
                    Log.Fatal($"Maptiler api key not valid, response status code: {response.StatusCode}");
                    Log.CloseAndFlush();
                    return 1;
                }

                Log.Warning($"Maptiler api returned status code {response.StatusCode}, starting anyway");
                response.Dispose();
                response = null;
            }

            if (response is not null)
            {
                var json = await JsonSerializer.DeserializeAsync<JsonObject>(response.Content.ReadAsStream());

                if (json is null || !json.TryGetPropertyValue("maxzoom", out JsonNode? maxZoomNode) || maxZoomNode is not JsonValue maxZoomValue || maxZoomValue.GetValueKind() != JsonValueKind.Number)
                {
                    Log.Warning("Invalid maptiler response");
                }
                else
                {
                    maxZoom = maxZoomValue.GetValue<int>();
                }

                Log.Information("Verified maptiler api key");
                response.Dispose();
            }

            tileDataSource = new MaptilerTileDataSource(options.MapTilerApiKey, maxZoom, httpClient);
        }
        else
        {
            Debug.Assert(options.TileDatabaseConnectionString is not null);

            Log.Information("Connecting to tile database");
            try
            {
                tileDataSource = new DatabaseTileDataSource(NpgsqlDataSource.Create(options.TileDatabaseConnectionString));
            }
            catch (Exception ex)
            {
                Log.Fatal($"Could not connect to tile database: {ex}");
                if (ex is ArgumentException)
                {
                    Log.Information($"The provided connection string is: '{options.TileDatabaseConnectionString}', make sure that it is in the correct format");
                }

                Log.CloseAndFlush();
                return 1;
            }

            Log.Information("Connected to tile database");
        }

        Log.Information("Connecting to event bus");
        EventBusClient eventBusClient;
        try
        {
            eventBusClient = await EventBusClient.ConnectAsync(options.EventBusConnectionString ?? "");
        }
        catch (EventBusClientException ex)
        {
            tileDataSource.Dispose();

            Log.Fatal($"Could not connect to event bus: {ex}");
            Log.CloseAndFlush();
            return 1;
        }

        Log.Information("Connected to event bus");

        Log.Information("Loading static data");
        StaticData.StaticData staticData;
        try
        {
            staticData = new StaticData.StaticData(options.StaticDataPath ?? "");
        }
        catch (StaticDataException staticDataException)
        {
            tileDataSource.Dispose();

            Log.Fatal($"Failed to load static data: {staticDataException}");
            Log.CloseAndFlush();
            return 1;
        }

        Log.Information("Loaded static data");

        await using (var renderer = new EventBusTileRenderer(tileDataSource, eventBusClient, staticData))
        {
            await renderer.RunAsync();
        }

        return 0;
    }
}