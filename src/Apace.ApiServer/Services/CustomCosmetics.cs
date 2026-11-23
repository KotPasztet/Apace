using Serilog;
using System.Text.Json;

namespace Apace.ApiServer.Services;

/// <summary>One custom cosmetic defined by the panel (Marketplace Management
/// page, custom cosmetics card) in <c>persona-catalog/custom-cosmetics.json</c>.
/// Shapes a PersonaDurable catalog offer item (CatalogController) and — when the
/// entry carries both a pack uuid and a content key — an encrypted-container
/// pack whose key rides the inventory receipt (InventoryController). Enabled is
/// the panel's per-item availability switch: null (a missing "enabled" key —
/// entries written before the switch existed) counts as enabled, false withholds
/// both the offer item and the entitlement.</summary>
public sealed record CustomCosmeticItem(
    string Id,
    string Title,
    string? Description,
    string PieceType,
    string PieceId,
    string Creator,
    string Rarity,
    int Price,
    List<string> Tags,
    string PackUuid,
    string ContentKey,
    string? ThumbnailId,
    bool? Enabled = null);

// Custom cosmetics store (panel-defined dressing room items).
//
// The panel writes <MojangAssetsDir>/persona-catalog/custom-cosmetics.json
// ({"items":[...]}) into the same volume directory that already holds the live
// persona catalog's index/probe state. This service is the ApiServer side: it
// reads the file, hot-reloads it whenever the file changes (mtime + size stamp)
// and exposes the items, a change stamp for offer caches, and the pack
// entitlement pairs the inventory receipt needs. The panel can withhold
// individual entries with an "enabled": false availability switch — disabled
// entries serve neither an offer item nor an entitlement. Read-only here — the
// file is owned by the panel; a missing or corrupt file simply serves an empty
// list.
public static class CustomCosmetics
{
    private const string FileName = "custom-cosmetics.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true, // the panel writes camelCase keys
    };

    private static readonly object StateLock = new();

    /// <summary>The panel's file inside the persona-catalog volume directory.</summary>
    public static string FilePath => Path.Combine(LivePersonaCatalog.CatalogDir, FileName);

    private sealed record CustomCosmeticsFile(List<CustomCosmeticItem>? Items);

    private static List<CustomCosmeticItem> _items = [];
    private static List<(string PackUuid, string ContentKey)> _entitlements = [];
    private static (long Mtime, long Length) _loadedStamp;
    private static ulong _stamp;

    public static IReadOnlyList<CustomCosmeticItem> Items
    {
        get
        {
            EnsureFresh();
            lock (StateLock)
            {
                return _items;
            }
        }
    }

    /// <summary>Changes whenever the file changes (file mtime UTC ticks; 0 when
    /// the file is absent) — offer caches key off it, like the catalog index stamp.</summary>
    public static ulong Stamp
    {
        get
        {
            EnsureFresh();
            lock (StateLock)
            {
                return _stamp;
            }
        }
    }

    /// <summary>One (PackUuid, ContentKey) pair per served item that defines
    /// both — merged into the GetInventoryItems receipt by InventoryController.
    /// Items hidden by the panel availability switch deliver no key.</summary>
    public static IReadOnlyList<(string PackUuid, string ContentKey)> Entitlements
    {
        get
        {
            EnsureFresh();
            lock (StateLock)
            {
                return _entitlements;
            }
        }
    }

    // Hot reload: stat the file on every access (one syscall) and reload only
    // when the (mtime, size) stamp changed — a panel edit is picked up on the
    // next request without a restart, and a missing file is just stamp (0, 0).
    private static void EnsureFresh()
    {
        (long Mtime, long Length) stamp;
        try
        {
            var info = new FileInfo(FilePath);
            stamp = info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0, 0);
        }
        catch (Exception)
        {
            stamp = (0, 0);
        }

        lock (StateLock)
        {
            if (stamp == _loadedStamp)
            {
                return;
            }

            _loadedStamp = stamp;
            _stamp = (ulong)stamp.Mtime;
            _items = LoadItems();
            _entitlements = [.. _items
                .Where(item => !string.IsNullOrWhiteSpace(item.PackUuid) && !string.IsNullOrWhiteSpace(item.ContentKey))
                .Select(item => (item.PackUuid, item.ContentKey))];

            if (_items.Count > 0)
            {
                Log.Logger.Information(
                    "Custom cosmetics loaded from {Path}: {Count} item(s), {Entitlements} content-key entitlement(s)",
                    FilePath, _items.Count, _entitlements.Count);
            }
            else if (stamp.Mtime != 0)
            {
                Log.Logger.Warning("Custom cosmetics file at {Path} holds no usable items — serving no custom cosmetics", FilePath);
            }
        }
    }

    private static List<CustomCosmeticItem> LoadItems()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return [];
            }

            var file = JsonSerializer.Deserialize<CustomCosmeticsFile>(File.ReadAllText(FilePath), JsonOptions);
            if (file?.Items is not { Count: > 0 })
            {
                return [];
            }

            var items = new List<CustomCosmeticItem>(file.Items.Count);
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int hidden = 0;
            foreach (var item in file.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.PieceType) || !Guid.TryParse(item.PieceId, out _))
                {
                    Log.Logger.Warning("Custom cosmetics: skipping an entry without id/pieceType/pieceId in {Path}", FilePath);
                    continue;
                }

                if (!seenIds.Add(item.Id))
                {
                    Log.Logger.Warning("Custom cosmetics: duplicate item id {Id} in {Path} — the first entry wins", item.Id, FilePath);
                    continue;
                }

                if (item.Enabled == false)
                {
                    // the panel's per-item availability switch — the entry is
                    // withheld from the offer items and (being derived from
                    // this list) from the receipt entitlements too
                    hidden++;
                    continue;
                }

                items.Add(item with
                {
                    Title = string.IsNullOrWhiteSpace(item.Title) ? item.Id : item.Title,
                    Tags = item.Tags ?? [],
                    // the catalog offer consumes both verbatim (PersonaData
                    // CreatorName → the pack-gate creator check, Rarity → the
                    // client's rarity filters) — an empty value must not leak
                    Creator = string.IsNullOrWhiteSpace(item.Creator) ? "KotPasztet" : item.Creator,
                    Rarity = string.IsNullOrWhiteSpace(item.Rarity) ? "common" : item.Rarity,
                });
            }

            if (hidden > 0)
            {
                Log.Logger.Information("Custom cosmetics: {Hidden} item(s) hidden by the panel availability switch", hidden);
            }

            return items;
        }
        catch (Exception ex)
        {
            // a corrupt file must never take the catalog down — serve nothing until it is fixed
            Log.Logger.Warning(ex, "Custom cosmetics file at {Path} could not be loaded — serving no custom cosmetics", FilePath);
            return [];
        }
    }
}
