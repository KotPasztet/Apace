namespace ViennaDotNet.StaticData;

public sealed class StaticData
{
    public readonly Catalog Catalog;
    public readonly PlayerLevels Levels;
    public readonly TappablesConfig TappablesConfig;
    public readonly EncountersConfig EncountersConfig;
    public readonly TileRenderer TileRenderer;
    public readonly Buildplates Buildplates;
    public readonly Playfab Playfab;

    public StaticData(string dir)
    {
        dir = Path.GetFullPath(dir);

        Catalog = new Catalog(Path.Combine(dir, "catalog"));
        Levels = new PlayerLevels(Path.Combine(dir, "levels"));
        TappablesConfig = new TappablesConfig(Path.Combine(dir, "tappables"));
        EncountersConfig = new EncountersConfig(Path.Combine(dir, "encounters"));
        TileRenderer = new TileRenderer(Path.Combine(dir, "tile_renderer"));
        Buildplates = new Buildplates(Path.Combine(dir, "buildplates"));
        Playfab = new Playfab(Path.Combine(dir, "playfab"));
    }
}