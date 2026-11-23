using System.Text.Json;

namespace Apace.LauncherUI.Services;

// Light-weight reader for the synced persona catalog index — the panel pages
// only need the thematic pack list to render the pack pickers; the full index
// model lives on the ApiServer side.
//
// Why theme tags and not pack bundles: the marketplace has no accessory pack
// entities. Every PersonaDurable entry carries exactly one packIdentity — the
// persona_piece itself (probe: scripts/probe_persona_tags.py, 1200 entries,
// zero multi-entry / non-piece identities) — and the MarketplaceDurableCatalog
// bundles are only skinpacks (classic skins), resourcepacks and worldtemplates.
// The marketplace's own thematic grouping of accessories is its tag.*
// metadata, so those tags are the gating packs; pieces without a theme tag are
// the base content that always serves.
public static class PersonaPackIndex
{
    // Shared with the ApiServer's PersonaPackGate.DefaultCategoryLimit (separate
    // processes share the volume, not the code) — keep both in sync.
    public const int DefaultCategoryLimit = 30;

    // One thematic pack: a marketplace theme tag (tag.*) and the persona pieces
    // carrying it. A pack spans every piece category it appears on (head, tops,
    // footwear…); enabling it serves all of them — no per-category toggles.
    public sealed record ThemePackRow(string Tag, string Title, int PieceCount, string Types);

    private sealed record IndexFile(int RawItemCount, int EmoteCount, List<IndexItem> Items);
    private sealed record IndexItem(string Id, string Title, string PieceType, List<string>? Tags, bool IsEmote);

    public static readonly JsonSerializerOptions JsonWebOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The thematic packs in the synced index (the pieces' marketplace theme
    /// tags, grouped), largest first, plus a one-line summary for the page
    /// header or null when the index is unusable. Type-ish tags (the piece type
    /// restated as a tag, e.g. tag.hair on hair pieces) don't form packs — they
    /// are excluded here AND on the ApiServer gate (LivePersonaCatalog.GetOffer
    /// — separate processes share the volume, not the code), or pieces tagged
    /// only with their own type would vanish when gating is on.
    /// </summary>
    public static (List<ThemePackRow> Packs, string? Summary) ReadThemePacks()
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

            var typeSuffixes = index.Items
                .Where(item => !item.IsEmote && item.PieceType.StartsWith("persona_", StringComparison.Ordinal))
                .Select(item => item.PieceType["persona_".Length..])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool IsThemeTag(string tag)
                => tag.StartsWith("tag.", StringComparison.OrdinalIgnoreCase)
                   && !typeSuffixes.Contains(tag["tag.".Length..]);

            var byTag = new Dictionary<string, (int Count, Dictionary<string, int> Types)>(StringComparer.OrdinalIgnoreCase);
            int themed = 0, baseCount = 0;
            foreach (var item in index.Items)
            {
                if (item.IsEmote)
                {
                    continue; // never offered — the dressing room has no emote category
                }

                List<string> themes = [.. (item.Tags ?? []).Where(IsThemeTag).Distinct(StringComparer.OrdinalIgnoreCase)];
                if (themes.Count == 0)
                {
                    baseCount++;
                    continue;
                }

                themed++;
                foreach (string tag in themes)
                {
                    (int Count, Dictionary<string, int> Types) entry = byTag.TryGetValue(tag, out var existing)
                        ? existing
                        : (0, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
                    entry.Count++;
                    entry.Types[item.PieceType] = entry.Types.TryGetValue(item.PieceType, out int count) ? count + 1 : 1;
                    byTag[tag] = entry;
                }
            }

            var packs = byTag
                .Select(kv => new ThemePackRow(kv.Key, ThemeTitle(kv.Key), kv.Value.Count, TypesSummary(kv.Value.Types)))
                .OrderByDescending(row => row.PieceCount)
                .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            string summary = packs.Count == 0
                ? "No theme tags in the synced index yet — run a full sync first (packs come from the pieces' own marketplace metadata)."
                : $"{packs.Count} thematic pack(s) from the marketplace's own theme tags — {themed} of {themed + baseCount} piece(s) are themed, "
                + $"{baseCount} base piece(s) without a theme tag always serve.";
            return (packs, summary);
        }
        catch (Exception ex)
        {
            return ([], $"Reading the thematic packs from the index failed: {ex.Message}");
        }
    }

    // "tag.halloween" → "Halloween"; "tag.character_customization" → "Character customization"
    private static string ThemeTitle(string tag)
    {
        string value = tag["tag.".Length..].Replace('_', ' ').Trim();
        return value.Length == 0 ? tag : char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string TypesSummary(Dictionary<string, int> types)
    {
        var top = types.OrderByDescending(kv => kv.Value).Take(3).ToList();
        int rest = types.Values.Sum() - top.Sum(kv => kv.Value);
        string body = string.Join(", ", top.Select(kv => $"{kv.Value} {PrettifyType(kv.Key)}"));
        return rest > 0 ? $"{body}, +{rest} more" : body;
    }

    private static string PrettifyType(string pieceType)
        => pieceType.StartsWith("persona_", StringComparison.Ordinal) ? pieceType["persona_".Length..] : pieceType;
}
