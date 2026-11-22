using System.Text.Json;

namespace Apace.LauncherUI.Services;

// Light-weight reader for the synced persona catalog index — the panel pages
// only need the per-category (piece type) counts to render the pack
// checklists; the full index model lives on the ApiServer side.
public static class PersonaPackIndex
{
    public sealed record PackCategory(string PieceType, int Count);

    private sealed record IndexFile(int RawItemCount, int EmoteCount, List<IndexItem> Items);
    private sealed record IndexItem(string Id, string Title, string PieceType, string? ThumbnailUrl, bool IsEmote);

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
}
