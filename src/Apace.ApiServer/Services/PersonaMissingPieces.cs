using System.Text.Json;
using Serilog;

namespace Apace.ApiServer.Services;

// The client-reported "piece missing from my repository" registry. The frozen
// client emits a PersonaCreationFailed telemetry event with Reason
// "PieceNotInRepository" and the piece uuid whenever its persona build walks a
// profile reference it cannot resolve — no catalog item anywhere (live index,
// staticdata, customs), no built-in piece, so nothing to download and the
// whole persona fails (the player sees default/broken cosmetics and the
// dressing room cannot equip anything for that avatar — "even flat things
// don't equip").
//
// The registry persists those uuids (persona-catalog/missing-pieces.json next
// to the catalog index) and the profile-serving path DROPS the matching piece
// references from served profiles: the client provably cannot use the piece,
// so keeping the reference only keeps failing the persona build. Dropping is
// reversible by design — entries expire (default 48 h), and the drop is
// skipped whenever the piece resolves server-side again (index re-sync, a
// manual mirror, a custom cosmetic) — so a piece that becomes available
// automatically comes back.
public static class PersonaMissingPieces
{
    private const string FileName = "missing-pieces.json";

    // A report older than this stops hiding the piece — the client re-reports
    // on the next persona build if the piece is still unresolvable.
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromHours(48);

    private sealed record MissingEntry(DateTimeOffset ReportedAt);

    private sealed class MissingFile
    {
        public Dictionary<string, MissingEntry> Pieces { get; set; } = [];
    }

    private static readonly object Lock = new();
    private static Dictionary<string, MissingEntry> Pieces = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;
    private static bool _dirty;
    private static DateTime _lastSave = DateTime.MinValue;

    private static string FilePath => Path.Combine(Program.MojangAssetsDir, "persona-catalog", FileName);

    private static void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var file = JsonSerializer.Deserialize<MissingFile>(File.ReadAllText(FilePath), JsonOptions);
            if (file?.Pieces is { } pieces)
            {
                foreach (var (pieceId, entry) in pieces)
                {
                    Pieces[pieceId] = entry;
                }

                Log.Information("Loaded {Count} client-reported missing persona piece(s) from {Path}", Pieces.Count, FilePath);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load the missing persona piece registry from {Path} — starts empty", FilePath);
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(new MissingFile { Pieces = Pieces }, JsonOptions));
            File.Move(tempPath, FilePath, overwrite: true);
            _dirty = false;
            _lastSave = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save the missing persona piece registry to {Path}", FilePath);
        }
    }

    /// <summary>Registers a client report that the piece uuid is unresolvable
    /// (PersonaCreationFailed / PieceNotInRepository). Deduplicated — one line
    /// per piece id per run.</summary>
    public static void Record(string pieceId, Serilog.ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(pieceId))
        {
            return;
        }

        lock (Lock)
        {
            Load();
            bool fresh = !Pieces.ContainsKey(pieceId);
            Pieces[pieceId] = new MissingEntry(DateTimeOffset.UtcNow);
            _dirty = true;
            if ((DateTime.UtcNow - _lastSave).TotalSeconds >= 15)
            {
                Save();
            }

            if (fresh)
            {
                bool resolvable = TryResolveServerSide(pieceId);
                Log.Information(
                    "Client reports persona piece {Piece} missing from its repository (PersonaCreationFailed PieceNotInRepository){Resolvable} — profile references to it are dropped from served profiles for {Hours}h so the persona build stops failing",
                    pieceId, resolvable ? " (it DOES resolve server-side now — the drop stays off)" : "", EntryLifetime.TotalHours);
            }
        }
    }

    /// <summary>True when the client PROVABLY cannot resolve the piece (a fresh
    /// report exists) AND the server cannot offer it either (no live-index /
    /// staticdata / custom-cosmetic item carries it) — dropping the reference
    /// from a served profile is then the only way that persona build succeeds.
    /// Expired reports stop hiding the piece.</summary>
    public static bool IsUnresolvable(string pieceId)
    {
        lock (Lock)
        {
            Load();
            if (!Pieces.TryGetValue(pieceId, out MissingEntry? entry)
                || DateTimeOffset.UtcNow - entry.ReportedAt > EntryLifetime)
            {
                return false;
            }

            return !TryResolveServerSide(pieceId);
        }
    }

    // A piece the server can actually provide (live index, staticdata or the
    // panel's custom cosmetics) is never dropped — a report against it was a
    // transient failure (dead probe, CDN hiccup) and the client will fetch it.
    private static bool TryResolveServerSide(string pieceId)
        => PersonaPieceTypes.TryResolvePiece(pieceId, out _);

    /// <summary>Flushes a pending registry write (called after telemetry bursts).</summary>
    public static void Flush(Serilog.ILogger logger)
    {
        lock (Lock)
        {
            if (_dirty)
            {
                Save();
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
