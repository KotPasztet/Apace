namespace Apace.StaticData;

public sealed class StaticData
{
    public readonly string Directory;

    // sibling of the staticdata directory in the repo checkout; null in deployments
    // (there publish.ps1 has already copied staticdata-extras INTO the staticdata tree)
    private string? _extrasDirectory;

    private Catalog? _catalog;
    private PlayerLevels? _levels;
    private TappablesConfig? _tappablesConfig;
    private EncountersConfig? _encountersConfig;
    private AdventuresConfig? _adventuresConfig;
    private TileRenderer? _tileRenderer;
    private Buildplates? _buildplates;
    private Playfab? _playfab;

    public StaticData(string dir)
    {
        Directory = Path.GetFullPath(dir);
    }

    /// <summary>
    /// The Apace staticdata-extras overlay subdirectory for a staticdata subdirectory
    /// (e.g. "adventures" → "&lt;staticdata&gt;/../staticdata-extras/adventures"), or null when
    /// the overlay does not exist in this layout.
    /// </summary>
    private string? ExtrasDir(string subDirectory)
    {
        if (_extrasDirectory is null)
        {
            string extrasDirectory = Path.GetFullPath(Path.Combine(Directory, "..", "staticdata-extras"));
            _extrasDirectory = System.IO.Directory.Exists(extrasDirectory) ? extrasDirectory : null;
        }

        return _extrasDirectory is null ? null : Path.Combine(_extrasDirectory, subDirectory);
    }

    public Catalog Catalog => _catalog ??= new Catalog(Path.Combine(Directory, "catalog"));

    public PlayerLevels Levels => _levels ??= new PlayerLevels(Path.Combine(Directory, "levels"));

    public TappablesConfig TappablesConfig => _tappablesConfig ??= new TappablesConfig(Path.Combine(Directory, "tappables"), ExtrasDir("tappables"));

    public EncountersConfig EncountersConfig => _encountersConfig ??= new EncountersConfig(Path.Combine(Directory, "encounters"));

    public AdventuresConfig AdventuresConfig => _adventuresConfig ??= new AdventuresConfig(Path.Combine(Directory, "adventures"), ExtrasDir("adventures"));

    public TileRenderer TileRenderer => _tileRenderer ??= new TileRenderer(Path.Combine(Directory, "tile_renderer"));

    public Buildplates Buildplates => _buildplates ??= new Buildplates(Path.Combine(Directory, "buildplates"), ExtrasDir("buildplates"));

    public Playfab Playfab => _playfab ??= new Playfab(Path.Combine(Directory, "playfab"));
}
