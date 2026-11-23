using System.Text.Json;

namespace Apace.LauncherUI.Services;

// Light-weight reader for the synced persona catalog index — the panel pages
// only need the per-category (piece type) counts to render the pack
// checklists; the full index model lives on the ApiServer side.
public static class PersonaPackIndex
{
    public sealed record PackCategory(string PieceType, int Count);

    public sealed record SkinPackRow(string ItemId, string Title, string PackUuid, int SkinCount);

    private sealed record IndexFile(int RawItemCount, int EmoteCount, List<IndexItem> Items, List<IndexSkinPack>? SkinPacks);
    private sealed record IndexItem(string Id, string Title, string PieceType, string? ThumbnailUrl, bool IsEmote);
    private sealed record IndexSkinPack(string Id, string Title, string PackUuid, int SkinCount);

    public static readonly JsonSerializerOptions JsonWebOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The wearable piece-type categories in the synced index (emotes excluded —
    /// the dressing room has no emote category), largest first, plus a one-line
    /// summary for the page header or null when the index is unusable.
    /// </summary>
    public static (List<PackCategory> Categories, string? Summary) ReadCategories()
    {
        try
        {
            string path = LivePersonaCatalogSync.IndexPath;
            if (!File.Exists(path))
            {
                return ([], "No synced persona catalog index yet — run a sync first (System Configuration → \"Sync now\", or wait for the scheduled sync).");
            }

            var index = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(path), JsonWebOptions);
            if (index?.Items is not { Count: > 0 })
            {
                return ([], "The synced index holds no items yet — run a sync first.");
            }

            var categories = index.Items
                .Where(item => !item.IsEmote)
                .GroupBy(item => item.PieceType)
                .Select(group => new PackCategory(group.Key, group.Count()))
                .OrderByDescending(category => category.Count)
                .ToList();
            string summary = $"Marketplace index: {index.Items.Count - index.EmoteCount} wearable piece(s) in {categories.Count} pack(s), "
                + $"{index.EmoteCount} emote(s) (never offered — the dressing room has no emote category).";
            return (categories, summary);
        }
        catch (Exception ex)
        {
            return ([], $"Reading the persona catalog index failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The thematic skin packs in the synced index (MarketplaceDurableCatalog_V1.2
    /// skinpack bundles — the dressing room's "Classic Skins" row), title-ordered.
    /// </summary>
    public static (List<SkinPackRow> Packs, string? Summary) ReadSkinPacks()
    {
        try
        {
            string path = LivePersonaCatalogSync.IndexPath;
            if (!File.Exists(path))
            {
                return ([], null);
            }

            var index = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(path), JsonWebOptions);
            if (index?.SkinPacks is not { Count: > 0 })
            {
                return ([], "No thematic skin packs in the synced index yet — run a sync first "
                    + "(System Configuration → \"Sync now\", or wait for the scheduled sync).");
            }

            var packs = index.SkinPacks
                .Select(row => new SkinPackRow(row.Id, row.Title, row.PackUuid, row.SkinCount))
                .OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return (packs, $"{packs.Count} thematic skin pack(s) indexed, {packs.Sum(row => row.SkinCount)} skin(s) total — "
                + "the offers behind the dressing room's \"Classic Skins\" row.");
        }
        catch (Exception ex)
        {
            return ([], $"Reading the skin packs from the index failed: {ex.Message}");
        }
    }
}
