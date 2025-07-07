using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using ViennaDotNet.Common;

namespace ViennaDotNet.StaticData;

public sealed class Playfab
{
    public readonly FrozenDictionary<Guid, Item> Items;
    public readonly FrozenDictionary<Guid, Item> ItemByEarthId;

    internal Playfab(string dir)
    {
        try
        {
            LinkedList<Item> items = [];
            foreach (string file in Directory.EnumerateFiles(Path.Combine(dir, "items")))
            {
                if (Path.GetExtension(file) != ".json")
                {
                    continue;
                }

                using (var stream = File.OpenRead(file))
                {
                    var item = JsonSerializer.Deserialize<Item>(stream);

                    Debug.Assert(item is not null);

                    items.AddLast(item);
                }
            }

            Items = items.ToFrozenDictionary(item => item.Id);
            ItemByEarthId = items.ToFrozenDictionary(item => item.Data switch
            {
                Item.BuildplateData bd => bd.Id,
                Item.InventoryItemData iid => iid.Id,
                _ => throw new UnreachableException(),
            });
        }
        catch (Exception exception)
        {
            throw new StaticDataException(null, exception);
        }
    }

    public sealed record Item(
        bool Purchasable,
        int Cost,
        Item.ItemData Data,
        string Title,
        string Description,
        string? ThumbnailImageId,
        DateTime CreationDate,
        DateTime LastModifiedDate,
        DateTime StartDate,
        Guid Id,
        Guid? FriendlyId,
        string SourceEntityId,
        string CreatorEntityId,
        IReadOnlyList<string> Tags,
        object Keywords,
        IReadOnlyList<object> Contents,
        IReadOnlyList<Item.ItemReference> ItemReferences,
        IReadOnlyDictionary<string, string> TitleTranslations,
        IReadOnlyDictionary<string, string> DescriptionTranslations
    )
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public enum Rarity
        {
            None,
            Common,
            Uncommon,
            Rare,
            Epic,
            Legendary,
        }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public enum BuidplateSize
        {
            Small,
            Medium,
            Large,
        }

        [JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
        [JsonDerivedType(typeof(BuildplateData), "Buildplate")]
        [JsonDerivedType(typeof(InventoryItemData), "InventoryItem")]
        public abstract class ItemData
        {
            public required string Version { get; init; }
            public required Rarity Rarity { get; init; }
        }

        public sealed class BuildplateData : ItemData
        {
            public required Guid Id { get; init; }

            public required BuidplateSize Size { get; init; }

            public required int UnlockLevel { get; init; }
        }

        public sealed class InventoryItemData : ItemData
        {
            public required Guid Id { get; init; }

            public required int Amount { get; init; }
        }

        public sealed record ItemReference(
            Guid Id,
            int Amount
        );
    }
}
