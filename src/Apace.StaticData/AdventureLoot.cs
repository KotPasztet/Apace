using System.Collections.Immutable;
using Apace.Common;

namespace Apace.StaticData;

public sealed class AdventureLoot
{
    private const double GroupTolerance = 1e-6;

    private readonly Dictionary<string, Table> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    // a broken loot file only takes out its own rarity
    internal AdventureLoot(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (string folderPath in Directory.EnumerateDirectories(dir))
            {
                string rarity = Path.GetFileName(folderPath);
                string lootFile = Path.Combine(folderPath, $"{rarity}-loot.json");
                if (!File.Exists(lootFile))
                {
                    continue;
                }

                try
                {
                    _tables[rarity] = Table.Load(lootFile);
                }
                catch (Exception exception)
                {
                    _errors[rarity] = $"{lootFile}: {exception.Message}";
                }
            }
        }
        catch (Exception exception)
        {
            _errors[""] = $"{dir}: {exception.Message}";
        }
    }

    public IReadOnlyDictionary<string, Table> Tables => _tables;

    // rarity (or "" for the folder itself) to the reason its loot could not be read
    public IReadOnlyDictionary<string, string> Errors => _errors;

    public Table? GetTable(string rarity)
        => _tables.GetValueOrDefault(rarity.Trim());

    public sealed class Table
    {
        public readonly ImmutableArray<Item> Items;

        private readonly ImmutableArray<Roll> _rolls;

        private Table(ImmutableArray<Item> items, ImmutableArray<Roll> rolls)
        {
            Items = items;
            _rolls = rolls;
        }

        internal static Table Load(string file)
        {
            LootFile? lootFile;
            using (var stream = File.OpenRead(file))
            {
                lootFile = Json.Deserialize<LootFile>(stream);
            }

            if (lootFile?.Items is not { Length: > 0 } items)
            {
                throw new InvalidDataException("No items");
            }

            return FromItems(items);
        }

        public static Table FromItems(IEnumerable<Item> items)
        {
            ImmutableArray<Item> itemArray = [.. items];
            List<Roll> rolls = [];
            Dictionary<string, (int RollIndex, List<Item> Items)> groups = [];
            foreach (Item item in itemArray)
            {
                if (string.IsNullOrWhiteSpace(item.Id))
                {
                    throw new InvalidDataException($"Item without an id ({item.Name})");
                }

                if (!(item.Chance > 0.0 && item.Chance <= 100.0))
                {
                    throw new InvalidDataException($"Chance {item.Chance} of {item.Id} is not above 0 and at most 100");
                }

                if (item.Min < 1 || item.Max < item.Min)
                {
                    throw new InvalidDataException($"Count {item.Min}-{item.Max} of {item.Id} is not valid");
                }

                if (string.IsNullOrWhiteSpace(item.Group))
                {
                    rolls.Add(new Roll([item], false));
                }
                else if (groups.TryGetValue(item.Group, out var group))
                {
                    group.Items.Add(item);
                }
                else
                {
                    // the group is rolled where its first item is, and filled in once all of its items are known
                    groups[item.Group] = (rolls.Count, [item]);
                    rolls.Add(new Roll([], false));
                }
            }

            foreach ((string name, (int rollIndex, List<Item> groupItems)) in groups)
            {
                double total = groupItems.Sum(item => item.Chance);
                if (total > 100.0 + GroupTolerance)
                {
                    throw new InvalidDataException($"Chances in group {name} add up to {total}, more than 100");
                }

                rolls[rollIndex] = new Roll([.. groupItems], total >= 100.0 - GroupTolerance);
            }

            return new Table(itemArray, [.. rolls]);
        }

        // at most one item per group drops, exactly one when the group's chances add up to 100
        public IReadOnlyList<Drop> RollDrops(Random random)
        {
            List<Drop> drops = [];
            foreach (Roll roll in _rolls)
            {
                Item? picked = null;
                double pick = random.NextDouble() * 100.0;
                foreach (Item item in roll.Items)
                {
                    pick -= item.Chance;
                    if (pick < 0.0)
                    {
                        picked = item;
                        break;
                    }
                }

                // rounding can leave a sliver above a complete group's total
                if (picked is null && roll.Complete)
                {
                    picked = roll.Items[^1];
                }

                if (picked is not null)
                {
                    drops.Add(new Drop(picked, random.Next(picked.Min, picked.Max + 1)));
                }
            }

            return drops;
        }

        private sealed record LootFile(
            Item[]? Items
        );

        private sealed record Roll(
            ImmutableArray<Item> Items,
            bool Complete
        );
    }

    public sealed record Item(
        string Id,
        string? Name,
        double Chance,
        int Min = 1,
        int Max = 1,
        string? Group = null,
        bool Uncertain = false
    );

    public sealed record Drop(
        Item Item,
        int Count
    );
}
