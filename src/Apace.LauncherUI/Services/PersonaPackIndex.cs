using System.Text.Json;

namespace Apace.LauncherUI.Services;

// Light-weight reader for the synced persona catalog index — the panel pages
// only need the pack list to render the pack pickers; the full index model
// lives on the ApiServer side.
//
// The marketplace has no accessory pack entities: every PersonaDurable entry
// carries exactly one packIdentity — the persona_piece itself (probe:
// scripts/probe_persona_tags.py, 1200 entries, zero multi-entry / non-piece
// identities) — so the marketplace's own thematic grouping of accessories is
// its tag.* metadata, and those tags are the selectable packs; Minecraft
// accessories serve only when one of their tags is selected (tagless
// accessories never serve; see PersonaPackGate.AllowedPackUuidsAsync on the
// ApiServer). Skins serve regardless of selection (persona_skin pieces and the
// MarketplaceDurableCatalog bundles are exempt from the gate), so a bundle
// holding only skins would be unselectable noise — the index's skinPacks array
// is not listed.
public static class PersonaPackIndex
{
    // Shared with the ApiServer's PersonaPackGate.DefaultCategoryLimit (separate
    // processes share the volume, not the code) — keep both in sync.
    public const int DefaultCategoryLimit = 45;

    // One selectable pack row: a marketplace theme tag (tag.*) and the persona
    // ACCESSORIES carrying it — Tag holds the tag, and the pack spans every
    // piece category it appears on (head, tops, footwear…); enabling it serves
    // all of them — no per-category toggles. PieceCount is the accessory count.
    public sealed record ThemePackRow(string Tag, string Title, int PieceCount, string Types);

    private sealed record IndexFile(int RawItemCount, int EmoteCount, List<IndexItem> Items);
    private sealed record IndexItem(string Id, string Title, string PieceType, List<string>? Tags, bool IsEmote);

    public static readonly JsonSerializerOptions JsonWebOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The accessory-bearing theme-tag packs in the synced index, largest first,
    /// plus a one-line summary for the page header or null when the index is
    /// unusable. Only tags carried by at least one ACCESSORY form packs — a tag
    /// whose pieces are all persona_skin/emotes would serve nothing selectable
    /// (skins are exempt from pack selection), so it is hidden. Type-ish tags
    /// (the piece type restated as a tag, e.g. tag.hair on hair pieces) don't
    /// form packs — they are excluded here AND on the ApiServer gate
    /// (LivePersonaCatalog.GetOffer — separate processes share the volume, not
    /// the code), or pieces tagged only with their own type would vanish when
    /// gating is on.
    /// </summary>
    public static (List<ThemePackRow> Packs, string? Summary) ReadThemePacks()
    {
        // Cached by (mtime, length) of the catalog index: the marketplace pages
        // call this on EVERY navigation and the index now holds ~12k synced
        // items — re-parsing made each visit take seconds. The returned list is
        // shared — treat it as read-only.
        string path = LivePersonaCatalogSync.IndexPath;
        FileInfo info = new(path);
        (DateTime Write, long Length) key = info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1);

        lock (CacheLock)
        {
            if (_themePacksCache is { } cached && key == (cached.Write, cached.Length))
            {
                return (cached.Packs, cached.Summary);
            }
        }

        (List<ThemePackRow> Packs, string? Summary) result = ReadThemePacksUncached();

        lock (CacheLock)
        {
            _themePacksCache = (key.Write, key.Length, result.Packs, result.Summary);
        }

        return result;
    }

    private static readonly object CacheLock = new();
    private static (DateTime Write, long Length, List<ThemePackRow> Packs, string? Summary)? _themePacksCache;

    private static (List<ThemePackRow> Packs, string? Summary) ReadThemePacksUncached()
    {
        try
        {
            string path = LivePersonaCatalogSync.IndexPath;
            if (!File.Exists(path))
            {
                return ([], "Marketplace catalog not indexed yet — run a sync first.");
            }

            var index = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(path), JsonWebOptions);
            if (index?.Items is not { Count: > 0 })
            {
                return ([], "Marketplace catalog index holds no items — run a sync first.");
            }

            var typeSuffixes = index.Items
                .Where(item => !item.IsEmote && item.PieceType.StartsWith("persona_", StringComparison.Ordinal))
                .Select(item => item.PieceType["persona_".Length..])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool IsThemeTag(string tag)
                => tag.StartsWith("tag.", StringComparison.OrdinalIgnoreCase)
                   && !typeSuffixes.Contains(tag["tag.".Length..]);

            var byTag = new Dictionary<string, (int Count, Dictionary<string, int> Types)>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in index.Items)
            {
                if (item.IsEmote
                    || item.PieceType.Equals("persona_skin", StringComparison.OrdinalIgnoreCase)
                    || item.PieceType.Equals("persona_emote", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // the picker lists ACCESSORY bundles only — a tag whose pieces are all skins/emotes gets no row (skins serve ungated, emotes are never offered)
                }

                List<string> themes = [.. (item.Tags ?? []).Where(IsThemeTag).Distinct(StringComparer.OrdinalIgnoreCase)];
                if (themes.Count == 0)
                {
                    continue; // tagless accessories form no selectable pack — they never serve
                }

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
                .Where(kv => kv.Value.Count > 0) // stay defensive: a bundle with 0 accessories would be unselectable noise
                .Select(kv => new ThemePackRow(kv.Key, ThemeTitle(kv.Key), kv.Value.Count, TypesSummary(kv.Value.Types)))
                .OrderByDescending(row => row.PieceCount)
                .ThenBy(row => row.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            string summary = packs.Count == 0
                ? "No accessory bundles found."
                : $"{packs.Count} accessory bundles found.";
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
