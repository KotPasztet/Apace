using System.Collections.Immutable;
using System.Diagnostics;
using Apace.Common;

namespace Apace.StaticData;

public sealed class TappablesConfig
{
    public readonly ImmutableArray<TappableConfig> Tappables;

    internal TappablesConfig(string dir, string? extrasDir = null)
    {
        try
        {
            // load the base configs, then apply the extras overlay per file (a file present in the
            // overlay replaces the base one with the same name; extras-only files are appended)
            List<TappableConfig> tappables = [];
            Dictionary<string, int> indexesByFileName = [];
            foreach (string file in EnumerateJsonFiles(dir))
            {
                indexesByFileName.Add(Path.GetFileName(file), tappables.Count);
                tappables.Add(LoadTappableConfig(file));
            }

            if (extrasDir is not null)
            {
                foreach (string file in EnumerateJsonFiles(extrasDir))
                {
                    var tappable = LoadTappableConfig(file);
                    if (indexesByFileName.TryGetValue(Path.GetFileName(file), out int index))
                    {
                        tappables[index] = tappable;
                    }
                    else
                    {
                        tappables.Add(tappable);
                    }
                }
            }

            Tappables = [.. tappables];

            foreach (TappableConfig tappableConfig in Tappables)

            {
                foreach (TappableConfig.DropSetR dropSet in tappableConfig.DropSets)
                {
                    foreach (string itemId in dropSet.Items)
                    {
                        if (!tappableConfig.ItemCounts.ContainsKey(itemId))
                        {
                            throw new StaticDataException($"Tappable config {tappableConfig.Icon} has no item count for item {itemId}");
                        }
                    }
                }
            }
        }
        catch (StaticDataException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new StaticDataException(null, exception);
        }
    }

    private static IEnumerable<string> EnumerateJsonFiles(string dir)
        => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Where(file => Path.GetExtension(file) == ".json")
            : [];

    private static TappableConfig LoadTappableConfig(string file)
    {
        using var stream = File.OpenRead(file);
        var tappable = Json.Deserialize<TappableConfig>(stream);

        Debug.Assert(tappable is not null);

        return tappable!;
    }

    public record TappableConfig(
        string Icon,
        TappableConfig.DropSetR[] DropSets,
        Dictionary<string, TappableConfig.ItemCount> ItemCounts
    )
    {
        public record DropSetR(
            string[] Items,
            int Chance
        );

        public record ItemCount(
            int Min,
            int Max
        );
    }
}
