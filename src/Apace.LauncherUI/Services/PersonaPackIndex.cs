using System.Text.Json;

namespace Apace.LauncherUI.Services;

// Light-weight reader for the synced persona catalog index — the panel pages
// only need the pack list to render the pack pickers; the full index model
// lives on the ApiServer side.
//
// Two pack kinds. The marketplace has no accessory pack entities: every
// PersonaDurable entry carries exactly one packIdentity — the persona_piece
// itself (probe: scripts/probe_persona_tags.py, 1200 entries, zero multi-entry /
// non-piece identities) — so the marketplace's own thematic grouping of
// accessories is its tag.* metadata, and those tags are the thematic packs;
// pieces without a theme tag are the base content that always serves. The
// MarketplaceDurableCatalog bundles (the index's skinPacks array, written by
// the sync) are the classic skin packs: they gate by their pack uuid
// (PersonaPackGate.AllowedPackUuidsAsync + CatalogController.LiveSkinPackItemsFor
// on the ApiServer), so their selectable value is the uuid string itself.
public static class PersonaPackIndex
{
    // Shared with the ApiServer's PersonaPackGate.DefaultCategoryLimit (separate
    // processes share the volume, not the code) — keep both in sync.
    public const int DefaultCategoryLimit = 30;

    // One selectable pack row. A thematic pack (IsSkinPack = false) is a
    // marketplace theme tag (tag.*) and the persona pieces carrying it — Tag
    // holds the tag, and the pack spans every piece category it appears on
    // (head, tops, footwear…); enabling it serves all of them — no
    // per-category toggles. A classic skin pack (IsSkinPack = true) is a
    // MarketplaceDurableCatalog bundle — Tag holds the pack uuid verbatim,
    // which is what the panel defaults and per-player grants must contain for
    // the ApiServer's pack gate to serve the pack.
    public sealed record ThemePackRow(string Tag, string Title, int PieceCount, string Types, bool IsSkinPack = false);

    private sealed record IndexFile(int RawItemCount, int EmoteCount, List<IndexItem> Items, List<IndexSkinPack>? SkinPacks);
    private sealed record IndexItem(string Id, string Title, string PieceType, List<string>? Tags, bool IsEmote);

    // Subset of the sync's SyncSkinPack record (separate processes share the
    // volume, not the code) — the panel only needs title, pack uuid and skin
    // count; JSON binds case-insensitively (JsonSerializerDefaults.Web).
    private sealed record IndexSkinPack(string Id, string Title, string PackUuid, int SkinCount);

    public static readonly JsonSerializerOptions JsonWebOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The packs in the synced index, largest first, plus a one-line summary for
    /// the page header or null when the index is unusable: the pieces' marketplace
    /// theme tags grouped into thematic packs, plus the classic skin packs from
    /// the index's skinPacks array (selectable by pack uuid). Type-ish tags (the
    /// piece type restated as a tag, e.g. tag.hair on hair pieces) don't form
    /// packs — they are excluded here AND on the ApiServer gate
    /// (LivePersonaCatalog.GetOffer — separate processes share the volume, not
    /// the code), or pieces tagged only with their own type would vanish when
    /// gating is on. Tolerates indexes from before the skinPacks array existed.
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

            // Classic skin packs ride along: the sync already deduped them per pack
            // uuid, but stay defensive (first wins). Only uuid-parseable packs are
            // selectable — the gate matches the uuid, and Tag carries it verbatim.
            var skinPackRows = (index.SkinPacks ?? [])
                .Where(pack => Guid.TryParse(pack.PackUuid, out _))
                .GroupBy(pack => pack.PackUuid, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(pack => new ThemePackRow(pack.PackUuid, pack.Title, pack.SkinCount, "classic skins", IsSkinPack: true))
                .ToList();

            var packs = skinPackRows
                .Concat(byTag.Select(kv => new ThemePackRow(kv.Key, ThemeTitle(kv.Key), kv.Value.Count, TypesSummary(kv.Value.Types))))
                .OrderByDescending(row => row.PieceCount)
                .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            string summary = packs.Count == 0
                ? "No packs in the synced index yet — run a full sync first (thematic packs come from the pieces' own marketplace metadata, classic skin packs from the skin-pack bundles)."
                : $"{skinPackRows.Count} classic skin pack(s) plus {packs.Count - skinPackRows.Count} thematic pack(s) from the marketplace's own theme tags — "
                + $"{themed} of {themed + baseCount} piece(s) are themed, {baseCount} base piece(s) without a theme tag always serve.";
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
