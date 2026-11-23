using Serilog;
using System.Text.Json;

namespace Apace.ApiServer.Services;

// Per-item compatibility verdict overrides (the compat lane / panel → ApiServer),
// <MojangAssetsDir>/persona-catalog/compat-verdicts.json:
//
//   { "items": { "<catalog item id>": "supported" | "risky" | "unknown" } }
//
// Contract — catalog metadata keyed by catalog item id (custom cosmetics use
// their panel-generated item id): "risky" refuses the piece (hidden from the
// offer, refused on equip while packs.json "onlySupported" is on);
// "supported" and "unknown" serve. This file is the explicit override channel:
// when the compat pipeline does not write it (the common case), every piece
// falls through to the geometry facts the metadata pipeline recorded in
// probe-state.json and PersonaPieceTypes.IsSupportedCosmetic classifies them —
// same band the panel's CosmeticCompatIndex uses. A piece with no evidence at
// all always serves (unknown → allowed: the filter only ever hides pieces with
// positive evidence of incompatibility). Hot-reloaded on (mtime, size) like
// CustomCosmetics; a missing or corrupt file serves no overrides.
public static class CosmeticCompatVerdicts
{
    private const string FileName = "compat-verdicts.json";

    private sealed record VerdictFile(Dictionary<string, string>? Items);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true, // the lane writes camelCase keys
    };

    private static readonly object StateLock = new();

    public static string FilePath => Path.Combine(LivePersonaCatalog.CatalogDir, FileName);

    private static Dictionary<string, string> _verdicts = new(StringComparer.OrdinalIgnoreCase);
    private static (long Mtime, long Length) _loadedStamp;

    /// <summary>The explicit verdict for one item id, or null when the store
    /// holds none for it (the caller falls back to the recorded geometry
    /// facts).</summary>
    public static string? Lookup(string itemId)
    {
        EnsureFresh();
        lock (StateLock)
        {
            return _verdicts.TryGetValue(itemId, out string? verdict) ? verdict : null;
        }
    }

    // Hot reload: stat the file on every access (one syscall) and reload only
    // when the (mtime, size) stamp changed — a lane edit is picked up on the
    // next request without a restart; a missing file is just stamp (0, 0).
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
            _verdicts = Load();
        }
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var file = JsonSerializer.Deserialize<VerdictFile>(File.ReadAllText(FilePath), JsonOptions);
            var verdicts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (itemId, verdict) in file?.Items ?? [])
            {
                if (!string.IsNullOrWhiteSpace(itemId) && !string.IsNullOrWhiteSpace(verdict))
                {
                    verdicts[itemId] = verdict.Trim();
                }
            }

            return verdicts;
        }
        catch (Exception ex)
        {
            // a corrupt file must never take the catalog down — no overrides until it is fixed
            Log.Warning(ex, "Compat verdict store at {Path} could not be loaded — verdicts fall back to the recorded geometry facts", FilePath);
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
