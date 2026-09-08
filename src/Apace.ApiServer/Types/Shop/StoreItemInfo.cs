using System.Text.Json.Serialization;
using Apace.ApiServer.Types.Buildplates;

namespace Apace.ApiServer.Types.Shop;

internal sealed record StoreItemInfo(
    Guid Id,
    StoreItemInfo.StoreItemTypeE StoreItemType,
    StoreItemInfo.StoreItemStatus? Status,
    uint StreamVersion,
    string? Model,
    Offset? BuildplateWorldOffset,
    Dimension? BuildplateWorldDimension,
    IReadOnlyDictionary<Guid, int>? InventoryCounts,
    Guid? FeaturedItem
)
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    internal enum StoreItemTypeE
    {
        Buildplates,
        Items
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    internal enum StoreItemStatus
    {
        Found,
        NotFound,
        NotModified
    }
}
