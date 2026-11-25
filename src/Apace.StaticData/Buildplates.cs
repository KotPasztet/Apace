namespace Apace.StaticData;

public sealed class Buildplates
{
    private const string ShopDirectory = "shop";

    private readonly string _directory;
    private readonly string? _extrasDirectory;

    internal Buildplates(string dir, string? extrasDir = null)
    {
        _directory = dir;
        _extrasDirectory = extrasDir;

        Directory.CreateDirectory(Path.Combine(_directory, ShopDirectory));
    }

    public IEnumerable<StaticBuidplate> ShopBuildplates
    {
        get
        {
            // the shop is the union of the base staticdata shop and the Apace extras overlay;
            // an extras zip with the same name replaces the base one
            List<StaticBuidplate> shopBuildplates = [];
            Dictionary<string, int> indexesByFileName = [];
            foreach (string path in Directory.EnumerateFiles(Path.Combine(_directory, ShopDirectory)))
            {
                indexesByFileName[Path.GetFileName(path)] = shopBuildplates.Count;
                shopBuildplates.Add(new StaticBuidplate(path));
            }

            if (_extrasDirectory is not null)
            {
                string extrasShopDirectory = Path.Combine(_extrasDirectory, ShopDirectory);
                if (Directory.Exists(extrasShopDirectory))
                {
                    foreach (string path in Directory.EnumerateFiles(extrasShopDirectory))
                    {
                        if (indexesByFileName.TryGetValue(Path.GetFileName(path), out int index))
                        {
                            shopBuildplates[index] = new StaticBuidplate(path);
                        }
                        else
                        {
                            shopBuildplates.Add(new StaticBuidplate(path));
                        }
                    }
                }
            }

            return shopBuildplates;
        }
    }
}
