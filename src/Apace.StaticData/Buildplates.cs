namespace Apace.StaticData;

public sealed class Buildplates
{
    private const string ShopDirectory = "shop";
    private const string AdventuresDirectory = "adventures";

    private readonly string _directory;

    internal Buildplates(string dir)
    {
        _directory = dir;

        Directory.CreateDirectory(Path.Combine(_directory, ShopDirectory));
    }

    public IEnumerable<StaticBuidplate> ShopBuildplates => Directory.EnumerateFiles(Path.Combine(_directory, ShopDirectory))
        .Select(path => new StaticBuidplate(path));

    public IEnumerable<StaticBuidplate> AdventureBuildplates
    {
        get
        {
            string directory = Path.Combine(_directory, AdventuresDirectory);
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.zip").Select(path => new StaticBuidplate(path))
                : [];
        }
    }
}
