using System.Globalization;
using System.Text.Json;

namespace Apace.LauncherUI.Services;

// Panel-side reader for the per-piece cosmetic compat profiles. The metadata
// pipeline (the ApiServer's probe/inspect pass and its successors) records the
// geometry facts it parsed out of every piece binary in
// <mojang-assets>/persona-catalog/probe-state.json under the "geometry" key,
// keyed by catalog item id — the same file the ApiServer's
// LivePersonaCatalog.TryGetGeometryCompat reads. The panel never parses piece
// binaries itself; it only classifies what the pipeline already recorded and
// shows the verdict as a badge next to each cosmetic (Marketplace Management
// page) plus feeds the "Allow only supported cosmetics" toggle explanation.
//
// Classification (keep in sync with the ApiServer's PersonaPieceTypes
// .IsSupportedCosmetic — separate processes share the volume, not the code):
//   supported  the piece files parse, every geometry variant format_version
//              1.12.0 up to (excluding) the maxGeometryFormatVersion cap
//              (packs.json, default 1.16.0), no animations / render controllers
//              / flipbook / particles and no face bones;
//   risky      geometry at or past the cap (UV rotation, transform pivots the
//              frozen client cannot render) or below 1.12.0 (legacy), or ANY
//              animation/effect signal, or face bones (rig 1.19.50/1.20.10,
//              MCPE-165232) — these render broken: floating parts, missing
//              meshes;
//   unknown    no profile (nothing scanned or nothing parsable in the zip) —
//              unknown pieces keep serving while the panel's "only supported"
//              switch is on; the filter only ever hides risky pieces.
public static class CosmeticCompatIndex
{
    public enum CosmeticCompatKind
    {
        // Proven renderable: in-band geometry, no animation/effect signals.
        Supported,

        // Positive evidence of breakage (new/legacy geometry, effects, face bones).
        Risky,

        // No profile — served even while "only supported" is on (no evidence of breakage).
        Unknown,
    }

    /// <summary>One piece's compat verdict: the badge kind plus a short reason
    /// for the tooltip. Immutability keeps the page's snapshot map stable.</summary>
    public sealed record CosmeticCompatInfo(CosmeticCompatKind Kind, string Detail);

    /// <summary>The verdict for pieces absent from the snapshot map (no profile yet).</summary>
    public static readonly CosmeticCompatInfo Unknown =
        new(CosmeticCompatKind.Unknown, "no compat profile yet — the metadata pipeline has not scanned this piece");

    // The supported geometry band: format_version 1.12.0 up to (excluding) the
    // max renders on the frozen Minecraft Earth client (the research note puts
    // the breakage at 1.16.0 — UV rotation / transform pivots); anything older
    // is legacy. The max is the same knob the ApiServer's PersonaPackGate
    // hot-reloads from packs.json ("maxGeometryFormatVersion") — mirrored here
    // so the badges show what the gate actually enforces.
    // MaxSupported is EXCLUSIVE (the band is [Min, Max)).
    private static readonly Version MinSupportedFormatVersion = new(1, 12, 0, 0);
    public static readonly Version DefaultMaxSupportedFormatVersion = new(1, 16, 0, 0);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // The "maxGeometryFormatVersion" half of packs.json (the panel writes the
    // file without it — the knob is a hand-edit) — stamp-cached like the
    // ApiServer's PersonaPackGate load, missing/unparsable falls back to the
    // default band.
    private sealed class PacksKnobs
    {
        public string? MaxGeometryFormatVersion { get; set; }
    }

    public static string PacksPath
        => Path.Combine(MojangAssetPaths.MojangAssetsDir, "persona-catalog", "packs.json");

    private static (long Mtime, long Length) _packsStamp;
    private static Version _maxGeometryFormatVersion = DefaultMaxSupportedFormatVersion;

    private static Version MaxGeometryFormatVersion
    {
        get
        {
            try
            {
                var info = new FileInfo(PacksPath);
                (long Mtime, long Length) stamp = info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0, 0);
                if (stamp != _packsStamp)
                {
                    _packsStamp = stamp;
                    var file = info.Exists ? JsonSerializer.Deserialize<PacksKnobs>(File.ReadAllText(PacksPath), JsonOptions) : null;
                    _maxGeometryFormatVersion = file?.MaxGeometryFormatVersion is { } knob && TryParseFormatVersion(knob, out Version parsed)
                        ? parsed
                        : DefaultMaxSupportedFormatVersion;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // a corrupt file must never break the display — keep the last value
            }

            return _maxGeometryFormatVersion;
        }
    }

    /// <summary>The pipeline's compat file — same path the ApiServer reads
    /// (LivePersonaCatalog.ProbeStatePath); missing or corrupt is tolerated.</summary>
    public static string ProbeStatePath
        => Path.Combine(MojangAssetPaths.MojangAssetsDir, "persona-catalog", "probe-state.json");

    // Only the "geometry" half of the probe-state file matters here. The extra
    // nullable flags are the forward-compat part of the pipeline contract: the
    // inspect pass records them once it scans for render controllers and face
    // bones; a file without them just leaves the flags null (not disqualifying
    // on their own — the unknown/no-profile rule covers missing evidence).
    private sealed class ProbeStateFile
    {
        public Dictionary<string, GeometryFacts>? Geometry { get; set; }
    }

    private sealed class GeometryFacts
    {
        public string? FormatVersion { get; set; }
        public bool? HasAnimations { get; set; }
        public bool? HasParticles { get; set; }
        public bool? HasRenderControllers { get; set; }
        public bool? HasFaceBones { get; set; }

        // The worst-case aggregation across the piece's geometry variants (one
        // per body size): FormatVersion is the HIGHEST parsed version, this the
        // LOWEST — a single legacy variant moves it below the band minimum.
        // Missing (older file) just skips the legacy-mix check.
        public string? LowestFormatVersion { get; set; }
    }

    /// <summary>
    /// A snapshot of every piece's compat verdict, keyed by catalog item id
    /// (custom cosmetics use their own panel-generated item id). A missing or
    /// corrupt file yields an empty map — every badge then shows unknown, and
    /// the page must never break over a file it only reads for display.
    /// </summary>
    public static Dictionary<string, CosmeticCompatInfo> ReadAll()
    {
        try
        {
            if (!File.Exists(ProbeStatePath))
            {
                return [];
            }

            var file = JsonSerializer.Deserialize<ProbeStateFile>(File.ReadAllText(ProbeStatePath), JsonOptions);
            var geometry = file?.Geometry;
            if (geometry is not { Count: > 0 })
            {
                return [];
            }

            var map = new Dictionary<string, CosmeticCompatInfo>(geometry.Count, StringComparer.OrdinalIgnoreCase);
            Version maxGeometry = MaxGeometryFormatVersion;
            foreach (var (itemId, facts) in geometry)
            {
                map[itemId] = Classify(facts, maxGeometry);
            }

            return map;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The verdict for one piece id — the map hit, or the shared
    /// unknown verdict when the pipeline has no profile for it yet.</summary>
    public static CosmeticCompatInfo Lookup(Dictionary<string, CosmeticCompatInfo> map, string itemId)
        => map.TryGetValue(itemId, out CosmeticCompatInfo? info) ? info : Unknown;

    private static CosmeticCompatInfo Classify(GeometryFacts? facts, Version maxGeometry)
    {
        if (facts is null)
        {
            return Unknown;
        }

        if (facts.FormatVersion is null
            || !TryParseFormatVersion(facts.FormatVersion, out Version version))
        {
            return new CosmeticCompatInfo(
                CosmeticCompatKind.Unknown,
                "compat profile present but the geometry format_version is missing or unparsable — the piece files do not parse");
        }

        if (TryParseFormatVersion(facts.LowestFormatVersion, out Version lowest) && lowest < MinSupportedFormatVersion)
        {
            return new CosmeticCompatInfo(
                CosmeticCompatKind.Risky,
                $"legacy geometry format_version {facts.LowestFormatVersion} — below 1.12.0 (worst of the piece's geometry variants)");
        }

        if (version >= maxGeometry)
        {
            return new CosmeticCompatInfo(
                CosmeticCompatKind.Risky,
                $"geometry format_version {facts.FormatVersion} — at or past the {maxGeometry} cap (UV rotation, transform pivots the frozen client cannot render)");
        }

        List<string> effects = [];
        if (facts.HasAnimations == true)
        {
            effects.Add("animations");
        }

        if (facts.HasParticles == true)
        {
            effects.Add("particles/flipbook");
        }

        if (facts.HasRenderControllers == true)
        {
            effects.Add("render controllers");
        }

        if (facts.HasFaceBones == true)
        {
            effects.Add("face bones (rig 1.19.50/1.20.10, MCPE-165232)");
        }

        if (effects.Count > 0)
        {
            return new CosmeticCompatInfo(
                CosmeticCompatKind.Risky,
                $"geometry format_version {facts.FormatVersion} but uses {string.Join(", ", effects)}");
        }

        return new CosmeticCompatInfo(
            CosmeticCompatKind.Supported,
            $"geometry format_version {facts.FormatVersion}, no animation/effect signals");
    }

    // Bedrock format versions are "1.21.0" / "1.16.200" style strings — parse
    // the leading numeric components and stop at the first non-numeric one
    // (tolerating pre-release suffixes and garbage); fewer than 4 components
    // pad with 0. Unparsable input returns false (unknown, not risky). Keep in
    // sync with PersonaPieceTypes.TryParseFormatVersion on the ApiServer.
    private static bool TryParseFormatVersion(string? formatVersion, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(formatVersion))
        {
            return false;
        }

        int[] components = new int[4];
        bool any = false;
        int parsed = 0;
        foreach (string part in formatVersion.Trim().Split('.'))
        {
            if (part.Length == 0
                || !int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                || value < 0)
            {
                break;
            }

            any = true;
            if (parsed < components.Length)
            {
                components[parsed++] = value;
            }
        }

        if (!any)
        {
            return false;
        }

        version = new Version(components[0], components[1], components[2], components[3]);
        return true;
    }
}
