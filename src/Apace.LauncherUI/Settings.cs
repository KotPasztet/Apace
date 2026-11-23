using Serilog;
using System.Net;
using System.Text.Json;
using Apace.Common.Utils;

namespace Apace.LauncherUI;

public sealed class Settings
{
    private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly Settings Default = new Settings()
    {
        ApiPort = 1808,
        EventBusPort = 5532,
        ObjectStorePort = 5396,
        IPv4 = "192.168.x.x",
        EarthDatabaseConnectionString = Path.Combine(Program.DataDirRelative, "earth.db"),
        LiveDatabaseConnectionString = Path.Combine(Program.DataDirRelative, "live.db"),
        EnableTileRenderingLabel = true,
        TileDataSource = TileDataSourceE.MapTiler,
        MapTilerApiKey = null,
        TileDatabaseConnectionString = "Host=localhost;Username=mylogin;Password=mypass;Database=genoa_tile_data",
        GeneratePreviewOnImport = true,
        SkipFileChecks = false,
        StaticDataPath = "../staticdata",
        LauncherBuildplatePreview = false,
        PlayableScale = 4,
        OnlyAllowLocalLogin = false,
        BridgePort = 19132,
        LivePersonaCatalog = true,
        PersonaCatalogSyncIntervalHours = 24,
    };

    public static Settings Instance { get; set; } = Default;

    public static string DefaultPath => "config.json";

    public ushort? ApiPort { get; set; }
    public ushort? EventBusPort { get; set; }
    public ushort? ObjectStorePort { get; set; }
    public string? IPv4 { get; set; }

    public string? EarthDatabaseConnectionString { get; set; }
    public string? LiveDatabaseConnectionString { get; set; }

    public bool? EnableTileRenderingLabel { get; set; }
    public TileDataSourceE? TileDataSource { get; set; }
    public string? MapTilerApiKey { get; set; }
    public string? TileDatabaseConnectionString { get; set; }

    public bool? GeneratePreviewOnImport { get; set; } // TODO: is this really needed?
    public bool? SkipFileChecks { get; set; }

    public string? StaticDataPath {get;set;}

    public bool? LauncherBuildplatePreview { get; set; }
    public int? PlayableScale { get; set; }

    public bool? OnlyAllowLocalLogin { get; set; }

    public ushort? BridgePort { get; set; }

    /// <summary>
    /// Live persona marketplace: the panel syncs the full PersonaDurable catalog
    /// from the still-live PlayFab marketplace (title 20ca2) into the data volume
    /// and the ApiServer serves it unlock-all instead of the dead legacy items.
    /// Default true.
    /// </summary>
    public bool? LivePersonaCatalog { get; set; }

    /// <summary>Hours between live persona catalog syncs (1..168). Default 24.</summary>
    public int? PersonaCatalogSyncIntervalHours { get; set; }

    /// <summary>
    /// Deprecated legacy flag — do not use. Pack filtering is now AUTO-GATING:
    /// packs.json carries no "gating" key and the ApiServer enforces filtering
    /// whenever the allowed pack set (PersonaDefaultPacks ∪ per-player grants)
    /// is non-empty, serving everything when that set is empty. Kept only so
    /// older config.json files still load and re-save without surprises; the
    /// panel no longer writes a value here (the Marketplace Management page
    /// explains the auto-gating rule in prose instead of a checkbox).
    /// </summary>
    public bool? PersonaPackGating { get; set; }

    /// <summary>
    /// Packs enabled for every player (marketplace theme tags for persona
    /// pieces, pack uuids for classic skin packs — both selectable in the
    /// Marketplace Management pack picker). Auto-gating: a non-empty set —
    /// together with the per-player grants — turns marketplace filtering on;
    /// an empty set serves the whole marketplace.
    /// </summary>
    public List<string>? PersonaDefaultPacks { get; set; }

    /// <summary>
    /// Max live marketplace pieces per dressing-room category GROUP — the
    /// client renders one tab per group (hoodies + outerwear count together;
    /// tops, capes and beards each count on their own where the client groups
    /// them), always applied, 0 = no limit. The marketplace has no per-category
    /// size control of its own and the frozen client chokes on huge category
    /// lists. Default 30; the ApiServer reads it from packs.json (hot-reloaded).
    /// </summary>
    public int? PersonaCategoryLimit { get; set; }

    /// <summary>
    /// Client-compatibility gate for the live marketplace (Marketplace
    /// Management page, "Allow only supported cosmetics"): when true, the
    /// ApiServer hides every piece whose compat profile carries positive
    /// evidence the frozen Minecraft Earth client cannot render it — geometry
    /// format_version ≥ 1.21.0 (UV rotation, transform pivots) or &lt; 1.12.0
    /// (legacy), any animation/effect signal, or face bones per rig
    /// 1.19.50/1.20.10 (MCPE-165232). Unknown pieces (no compat profile yet)
    /// keep serving — the filter only ever hides risky pieces, never unknown
    /// ones. Default true — treat null as true; the ApiServer reads it from
    /// packs.json ("onlySupported", hot-reloaded).
    /// </summary>
    public bool? PersonaOnlySupportedCosmetics { get; set; }

    public enum TileDataSourceE
    {
        MapTiler,
        PostgreSQL,
    }

    public void Save(string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this, jsonOptions));

    public async Task SaveAsync(string path)
    {
        using (var fs = File.OpenWriteNew(path))
        {
            await JsonSerializer.SerializeAsync(fs, this, jsonOptions);
        }
    }

    public static async Task<Settings> LoadAsync(string path)
    {
        Log.Information("Loading settings...");

        Settings? settings;

        if (!File.Exists(path))
        {
            Log.Information($"Config file doesn't exist, created default");
            settings = Default;
        }
        else
        {
            try
            {
                using (var fs = File.OpenRead(path))
                {
                    settings = await JsonSerializer.DeserializeAsync<Settings>(fs, jsonOptions);
                }

                if (settings is null)
                {
                    throw new InvalidDataException("Settings is null");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Error when parsing settings, using default: {ex}");
                settings = Default;
            }
        }

        if (settings.ApiPort is null)
        {
            Log.Warning($"Api port is invalid, using default: '{Default.ApiPort}'");
            settings.ApiPort = Default.ApiPort;
        }

        if (settings.EventBusPort is null)
        {
            Log.Warning($"EventBus port is invalid, using default: '{Default.EventBusPort}'");
            settings.EventBusPort = Default.EventBusPort;
        }

        if (settings.ObjectStorePort is null)
        {
            Log.Warning($"ObjectStore port is invalid, using default: '{Default.ObjectStorePort}'");
            settings.ObjectStorePort = Default.ObjectStorePort;
        }

        UriHostNameType nameType = Uri.CheckHostName(settings.IPv4);

        if (nameType != UriHostNameType.IPv4 && nameType != UriHostNameType.Dns)
        {
            Log.Warning($"IPv4 is invalid, using default: '{Default.IPv4}' (Change this in Options/IPv4)");
            settings.IPv4 = Default.IPv4;
        }

        if (string.IsNullOrWhiteSpace(settings.EarthDatabaseConnectionString))
        {
            Log.Warning($"DatabaseConnectionString is invalid, using default: '{Default.EarthDatabaseConnectionString}'");
            settings.EarthDatabaseConnectionString = Default.EarthDatabaseConnectionString;
        }

        if (string.IsNullOrWhiteSpace(settings.LiveDatabaseConnectionString))
        {
            Log.Warning($"LiveDatabaseConnectionString is invalid, using default: '{Default.LiveDatabaseConnectionString}'");
            settings.LiveDatabaseConnectionString = Default.LiveDatabaseConnectionString;
        }

        if (settings.EnableTileRenderingLabel is null)
        {
            Log.Warning($"EnableTileRenderingLabel is invalid, using default: '{Default.EnableTileRenderingLabel}'");
            settings.EnableTileRenderingLabel = Default.EnableTileRenderingLabel;
        }

        if (settings.EnableTileRenderingLabel is true)
        {
            if (settings.TileDataSource is null)
            {
                Log.Warning($"TileDataSource is invalid, using default: '{Default.TileDataSource}'");
                settings.TileDataSource = Default.TileDataSource;
            }

            if (string.IsNullOrWhiteSpace(settings.MapTilerApiKey))
            {
                Log.Warning($"MapTilerApiKey is invalid, using default: '{Default.MapTilerApiKey}'");
                settings.MapTilerApiKey = Default.MapTilerApiKey;
            }
        }

        if (settings.TileDatabaseConnectionString is null)
        {
            Log.Warning($"TileDatabaseConnectionString is invalid, using default: '{Default.TileDatabaseConnectionString}'");
            settings.TileDatabaseConnectionString = Default.TileDatabaseConnectionString;
        }

        if (settings.GeneratePreviewOnImport is null)
        {
            Log.Warning($"Generate preview on import is invalid, using default: '{Default.GeneratePreviewOnImport}'");
            settings.GeneratePreviewOnImport = Default.GeneratePreviewOnImport;
        }

        if (settings.SkipFileChecks is null)
        {
            Log.Warning($"Skip file checks is invalid, using default: '{Default.SkipFileChecks}'");
            settings.SkipFileChecks = Default.SkipFileChecks;
        }

        if (string.IsNullOrWhiteSpace(settings.StaticDataPath))
        {
            Log.Warning($"StaticData path is invalid, using default: '{Default.StaticDataPath}'");
            settings.StaticDataPath = Default.StaticDataPath;
        }

        if (settings.PlayableScale is not (>= 1 and <= 32))
        {
            Log.Warning($"Playable scale is invalid, using default: '{Default.PlayableScale}'");
            settings.PlayableScale = Default.PlayableScale;
        }

        if (settings.OnlyAllowLocalLogin is null)
        {
            Log.Warning($"OnlyAllowLocalLogin is invalid, using default: '{Default.OnlyAllowLocalLogin}'");
            settings.OnlyAllowLocalLogin = Default.OnlyAllowLocalLogin;
        }

        if (settings.BridgePort is null)
        {
            Log.Warning($"Bridge port is invalid, using default: '{Default.BridgePort}'");
            settings.BridgePort = Default.BridgePort;
        }

        if (settings.LivePersonaCatalog is null)
        {
            Log.Warning($"LivePersonaCatalog is invalid, using default: '{Default.LivePersonaCatalog}'");
            settings.LivePersonaCatalog = Default.LivePersonaCatalog;
        }

        if (settings.PersonaCatalogSyncIntervalHours is not (>= 1 and <= 168))
        {
            Log.Warning($"PersonaCatalogSyncIntervalHours is invalid, using default: '{Default.PersonaCatalogSyncIntervalHours}'");
            settings.PersonaCatalogSyncIntervalHours = Default.PersonaCatalogSyncIntervalHours;
        }

        Log.Information("Loaded settings");

        await settings.SaveAsync(path);

        return settings;
    }
}
