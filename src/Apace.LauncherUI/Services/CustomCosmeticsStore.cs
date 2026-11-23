using System.Security.Cryptography;
using System.Text.Json;

namespace Apace.LauncherUI.Services;

// Panel-side store for the custom persona cosmetics ("Custom cosmetics" card on
// the Marketplace Management page). The ApiServer reads the same JSON from the
// shared data volume (hot-reloaded, no restart) to build the catalog offers and
// the entitlement receipt content keys — the panel only writes it, so every
// write must be atomic (temp file + File.Move) and the asset files must land
// exactly where the CDN controllers look for them.
//
// Layout inside the mojang-assets volume:
//   <mojang-assets>/persona-catalog/custom-cosmetics.json  this store
//   <mojang-assets>/playfab/persona/<itemId>.zip           piece binary (PersonaCdnController)
//   <mojang-assets>/playfab/images/<thumbnailId>.png       thumbnail (canonical location)
//   <mojang-assets>/images/<thumbnailId>.png               thumbnail mirror — the existing
//                                                          MojangImagesCdnController serves
//                                                          /playfab/images/<id>.png from here,
//                                                          so a thumbnail is only visible to
//                                                          the client when this copy exists too
//
// Entry ids, piece ids, pack uuids and thumbnail ids are GUIDs the panel
// generates; they double as the asset file names. Anything that does not parse
// as a GUID (hand-edited JSON) is rejected before it can reach a file path —
// that is the path traversal guard for the remove/cleanup operations.
public static class CustomCosmeticsStore
{
    private sealed record CustomCosmeticsFile(List<CustomCosmeticItem>? Items);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Serialized JSON mutations: two admin tabs must not interleave
    // save/remove sequences into a torn file (the asset file operations work on
    // per-item paths and stay outside the lock).
    private static readonly SemaphoreSlim StoreLock = new(1, 1);

    public const long MaxZipBytes = 25 * 1024 * 1024;        // real piece zips are ~0.1-1 MB
    public const long MaxThumbnailBytes = 5 * 1024 * 1024;

    public sealed record CustomCosmeticItem(
        string Id,
        string Title,
        string Description,
        string PieceType,
        string PieceId,
        string Creator,
        string Rarity,
        int Price,
        List<string>? Tags,
        string PackUuid,
        string ContentKey,
        string? ThumbnailId)
    {
        /// <summary>
        /// Per-item availability switch (Marketplace Management table): false
        /// hides the piece from the marketplace offer AND drops its
        /// (PackUuid, ContentKey) entitlement pair, without touching the asset
        /// files — a "Show to players" toggle brings it straight back. Defaults
        /// to true, so entries written before the switch existed keep serving;
        /// the ApiServer record treats a missing "enabled" key as enabled too.
        /// </summary>
        public bool Enabled { get; init; } = true;
    }

    // Resolved per use — Program.DataDir is set after static init (same caveat
    // as the ThumbnailDirs lookup in PlayerSkins.razor).
    public static string CatalogDir => Path.Combine(MojangAssetGate.MojangAssetsDir, "persona-catalog");
    public static string StorePath => Path.Combine(CatalogDir, "custom-cosmetics.json");
    public static string PersonaDir => Path.Combine(MojangAssetGate.MojangAssetsDir, "playfab", "persona");
    public static string[] ThumbnailDirs =>
    [
        Path.Combine(MojangAssetGate.MojangAssetsDir, "playfab", "images"),
        Path.Combine(MojangAssetGate.MojangAssetsDir, "images"),
    ];

    /// <summary>
    /// The stored items, or an empty list when the file is missing. A corrupt
    /// file also yields an empty list plus the parse error for the UI — saving
    /// afterwards overwrites it.
    /// </summary>
    public static (List<CustomCosmeticItem> Items, string? Error) ReadAll()
    {
        try
        {
            if (!File.Exists(StorePath))
            {
                return ([], null);
            }

            var file = JsonSerializer.Deserialize<CustomCosmeticsFile>(File.ReadAllText(StorePath), JsonOptions);
            return (file?.Items ?? [], null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return ([], $"Reading custom-cosmetics.json failed: {ex.Message} — the next save overwrites it.");
        }
    }

    /// <summary>
    /// Writes the item list atomically (temp file + File.Move) so the
    /// hot-reloading server never observes a partial file.
    /// </summary>
    public static async Task SaveItemsAsync(IReadOnlyList<CustomCosmeticItem> items)
    {
        await StoreLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(CatalogDir);
            string tempPath = StorePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(new CustomCosmeticsFile([.. items]), JsonOptions));
            File.Move(tempPath, StorePath, overwrite: true);
        }
        finally
        {
            StoreLock.Release();
        }
    }

    public static string ZipPath(string itemId)
        => GuardedAssetPath(PersonaDir, itemId, ".zip");

    public static bool ZipExists(string itemId)
    {
        try
        {
            return File.Exists(ZipPath(itemId));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Both on-disk thumbnail locations for one thumbnail id.</summary>
    public static string[] ThumbnailPaths(string thumbnailId)
        => [.. ThumbnailDirs.Select(dir => GuardedAssetPath(dir, thumbnailId, ".png"))];

    public static bool ThumbnailExists(string thumbnailId)
    {
        try
        {
            return ThumbnailPaths(thumbnailId).Any(File.Exists);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 64 hex chars = 32 random bytes — the AES-256 content key delivered to the
    /// client through the entitlement receipt (the InventoryController
    /// ContentKey channel, same shape as the Apace Cape's fixed key).
    /// </summary>
    public static string NewContentKey()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>
    /// Stores the piece zip as <c>persona/&lt;itemId&gt;.zip</c> (temp + move, so
    /// the CDN never serves a half-written file).
    /// </summary>
    public static async Task SaveZipAsync(string itemId, Stream content)
    {
        Directory.CreateDirectory(PersonaDir);
        await WriteAtomicAsync(ZipPath(itemId), content);
    }

    /// <summary>
    /// Stores the thumbnail PNG in BOTH thumbnail directories (canonical
    /// playfab/images plus the images/ mirror the CDN controller serves). The
    /// browser stream is buffered once — IBrowserFile streams are not seekable,
    /// so both copies are written from the same memory buffer (thumbnails are
    /// capped at MaxThumbnailBytes).
    /// </summary>
    public static async Task SaveThumbnailAsync(string thumbnailId, Stream content)
    {
        Directory.CreateDirectory(ThumbnailDirs[0]);
        Directory.CreateDirectory(ThumbnailDirs[1]);
        using var buffered = new MemoryStream();
        await content.CopyToAsync(buffered);
        foreach (string path in ThumbnailPaths(thumbnailId))
        {
            buffered.Position = 0;
            await WriteAtomicAsync(path, buffered);
        }
    }

    /// <summary>
    /// Deletes the piece zip and both thumbnail copies (best-effort). Returns
    /// one message per file that could not be removed — or per id that is not a
    /// GUID (hand-edited JSON), in which case the asset files are deliberately
    /// left alone and only the catalog entry is removed by the caller.
    /// </summary>
    public static List<string> DeleteAssets(string itemId, string? thumbnailId)
    {
        var errors = new List<string>();
        if (!Guid.TryParse(itemId, out _))
        {
            errors.Add($"item id '{itemId}' is not a GUID — asset files left alone");
            return errors;
        }

        try
        {
            File.Delete(ZipPath(itemId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{itemId}.zip: {ex.Message}");
        }

        if (thumbnailId is not null)
        {
            errors.AddRange(DeleteThumbnails(thumbnailId));
        }

        return errors;
    }

    /// <summary>
    /// Deletes only the thumbnail copies of one thumbnail id (used when a
    /// replacement thumbnail retires the old one — the piece zip must survive).
    /// A null id (item without a thumbnail) is a no-op.
    /// </summary>
    public static List<string> DeleteThumbnails(string? thumbnailId)
    {
        var errors = new List<string>();
        if (thumbnailId is null || !Guid.TryParse(thumbnailId, out _))
        {
            if (thumbnailId is not null)
            {
                errors.Add($"thumbnail id '{thumbnailId}' is not a GUID — asset files left alone");
            }

            return errors;
        }

        foreach (string path in ThumbnailPaths(thumbnailId))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return errors;
    }

    // A GUID file name cannot carry path separators, and normalizing through
    // Guid.ToString() (lowercase "d" format, exactly how the panel names the
    // files it writes) plus the full-path prefix check keeps even a hand-edited
    // custom-cosmetics.json from pointing the asset operations outside the
    // asset directories.
    private static string GuardedAssetPath(string directory, string fileName, string extension)
    {
        if (!Guid.TryParse(fileName, out Guid guid))
        {
            throw new ArgumentException($"asset id '{fileName}' is not a GUID");
        }

        string fullPath = Path.GetFullPath(Path.Combine(directory, guid.ToString() + extension));
        string fullDirectory = Path.GetFullPath(directory);
        if (!fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"asset path for '{fileName}' escapes {directory}");
        }

        return fullPath;
    }

    private static async Task WriteAtomicAsync(string path, Stream content)
    {
        string tempPath = path + ".part";
        await using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await content.CopyToAsync(target);
        }

        File.Move(tempPath, path, overwrite: true);
    }
}
