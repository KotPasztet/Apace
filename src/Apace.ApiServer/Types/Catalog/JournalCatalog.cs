using static Apace.ApiServer.Types.Catalog.JournalCatalog;

namespace Apace.ApiServer.Types.Catalog;

public sealed record JournalCatalog(
    Dictionary<string, Item> Items
)
{
    public sealed record Item(
        string ReferenceId,
        string ParentCollection,
        int OverallOrder,
        int CollectionOrder,
        string? DefaultSound,
        bool Deprecated,
        string ToolsVersion
    );
}
