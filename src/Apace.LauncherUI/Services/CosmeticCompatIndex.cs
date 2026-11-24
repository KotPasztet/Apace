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
// .EvaluateCosmetic — separate processes share the volume, not the code):
//   supported  the piece files parse, every geometry variant format_version
//              up to (excluding) the maxGeometryFormatVersion cap (packs.json,
//              default 1.16.0), no animations / render controllers / flipbook
//              / particles and no face bones;
//   risky      geometry at or past the cap (UV rotation, transform pivots the
//              frozen client cannot render), or ANY animation/effect signal,
//              or face bones on a FACE ACCESSORY
//              (rig 1.19.50/1.20.10, MCPE-165232) — these render broken:
//              floating parts, missing meshes. Face bones are scoped to
//              persona_face_accessory pieces: hats, hoods and helmets
//              legitimately carry hat/head rig bones the frozen client renders
//              fine, so other piece types never turn risky on them;
//   auto-      custom rig bones on a head-family piece AND nothing else risky
//   repairable AND the packs.json "experimentalAutoRepairRigs" toggle is on —
//              the ApiServer's serve-time repairer re-parents the custom
//              bones onto "head" so they render attached (oversized bounds
//              or any other signal keeps the piece plain risky — the repair
//              fixes bones only);
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

        // The ONLY breakage evidence is custom rig bones on a head-family piece
        // and the experimental auto-repair toggle is ON: the ApiServer re-parents
        // the custom bones onto "head" at serve time (PersonaRigRepairer) and the
        // piece serves. Badge = "auto-repairable". (When the toggle is off the
        // same piece classifies plain Risky.)
        AutoRepairable,

        // No profile — served even while "only supported" is on (no evidence of breakage).
        Unknown,
    }

    /// <summary>One piece's compat verdict: the badge kind plus a short reason
    /// for the tooltip. Immutability keeps the page's snapshot map stable.</summary>
    public sealed record CosmeticCompatInfo(CosmeticCompatKind Kind, string Detail);

    /// <summary>The verdict for pieces absent from the snapshot map (no profile yet).</summary>
    public static readonly CosmeticCompatInfo Unknown =
        new(CosmeticCompatKind.Unknown, "no compat profile yet — the metadata pipeline has not scanned this piece");

    // The supported geometry band: format_version up to (excluding) the max
    // renders on the frozen Minecraft Earth client (the research note puts
    // the breakage at 1.16.0 — UV rotation / transform pivots). There is
    // deliberately NO floor: legacy 1.8.0 geometry renders fine — the Apace
    // capes (and MerelyCapes against the live persona pipeline) ship it
    // proven-working, so a floor only mass flagged renderable pieces
    // unsupported. The max is the same knob the ApiServer's PersonaPackGate
    // hot-reloads from packs.json ("maxGeometryFormatVersion") — mirrored here
    // so the badges show what the gate actually enforces.
    // MaxSupported is EXCLUSIVE (the band is [legacy, Max)).
    public static readonly Version DefaultMaxSupportedFormatVersion = new(1, 16, 0, 0);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // The "maxGeometryFormatVersion" + "experimentalAutoRepairRigs" +
    // "customSkeletonMode" halves of packs.json (the panel writes the file
    // itself — these ride along in the same save) — stamp-cached like the
    // ApiServer's PersonaPackGate load, missing/unparsable falls back to the
    // defaults.
    private sealed class PacksKnobs
    {
        public string? MaxGeometryFormatVersion { get; set; }
        public bool? ExperimentalAutoRepairRigs { get; set; }
        public string? CustomSkeletonMode { get; set; }
    }

    public static string PacksPath
        => Path.Combine(MojangAssetPaths.MojangAssetsDir, "persona-catalog", "packs.json");

    private static (long Mtime, long Length) _packsStamp;
    private static Version _maxGeometryFormatVersion = DefaultMaxSupportedFormatVersion;
    private static bool _experimentalAutoRepairRigs;
    private static string _customSkeletonMode = "off";

    private static void EnsureKnobsLoaded()
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
                _experimentalAutoRepairRigs = file?.ExperimentalAutoRepairRigs ?? false;
                _customSkeletonMode = file?.CustomSkeletonMode?.Trim().ToLowerInvariant() switch
                {
                    "strip" => "strip",
                    "flatonly" or "flat-only" or "flat" => "flatOnly",
                    _ => "off",
                };
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // a corrupt file must never break the display — keep the last values
        }
    }

    private static Version MaxGeometryFormatVersion
    {
        get
        {
            EnsureKnobsLoaded();
            return _maxGeometryFormatVersion;
        }
    }

    /// <summary>The packs.json "experimentalAutoRepairRigs" knob (default
    /// false), mirrored from the ApiServer's PersonaPackGate — when on, the
    /// ApiServer re-parents custom rig bones of head-family pieces whose ONLY
    /// risk signal they are onto "head" at serve time, so the badge shows
    /// auto-repairable instead of plain risky for them.</summary>
    private static bool ExperimentalAutoRepairRigs
    {
        get
        {
            EnsureKnobsLoaded();
            return _experimentalAutoRepairRigs;
        }
    }

    /// <summary>The packs.json "customSkeletonMode" knob (default off),
    /// mirrored from the ApiServer's PersonaPackGate — "strip"/"flatOnly"
    /// remove the custom bones from served geometry (Flat-only additionally
    /// hides custom-skeleton pieces from the offer), so custom-bone pieces
    /// badge as auto-repairable under either knob.</summary>
    private static string CustomSkeletonMode
    {
        get
        {
            EnsureKnobsLoaded();
            return _customSkeletonMode;
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

        // recorded since probe schema v5 — displaced-render signals for the
        // head family; older fact files leave them null (unknown, serving)
        public float? MaxVisibleBoundsWidth { get; set; }
        public float? MaxVisibleBoundsHeight { get; set; }
        public bool? HasNonstandardBones { get; set; }

        // recorded since probe schema v6 — a meta geometry_sources texture
        // reference no zip entry carries (the piece renders untextured)
        public bool? HasTextureBindingIssue { get; set; }
    }

    /// <summary>
    /// A snapshot of every piece's compat verdict, keyed by catalog item id
    /// (custom cosmetics use their own panel-generated item id). A missing or
    /// corrupt file yields an empty map — every badge then shows unknown, and
    /// the page must never break over a file it only reads for display.
    /// </summary>
    // Cached by (mtime, length) of both source files: the marketplace pages
    // re-read this on EVERY navigation, and the probe-state + index files grew
    // into the megabytes (12k pieces probed) — re-parsing both JSONs made each
    // page visit take seconds. The returned map is shared — treat it as
    // read-only (use Lookup; never mutate).
    private static readonly object CacheLock = new();
    private static (DateTime ProbeWrite, long ProbeLength, DateTime IndexWrite, long IndexLength, DateTime PacksWrite, Dictionary<string, CosmeticCompatInfo> Map)? _cache;

    public static Dictionary<string, CosmeticCompatInfo> ReadAll()
    {
        FileInfo probeInfo = new(ProbeStatePath);
        FileInfo indexInfo = new(IndexPath);
        FileInfo packsInfo = new(PacksPath);
        (DateTime, long, DateTime, long, DateTime) key = (
            probeInfo.Exists ? probeInfo.LastWriteTimeUtc : DateTime.MinValue,
            probeInfo.Exists ? probeInfo.Length : -1,
            indexInfo.Exists ? indexInfo.LastWriteTimeUtc : DateTime.MinValue,
            indexInfo.Exists ? indexInfo.Length : -1,
            packsInfo.Exists ? packsInfo.LastWriteTimeUtc : DateTime.MinValue);

        lock (CacheLock)
        {
            if (_cache is { } cached
                && key == (cached.ProbeWrite, cached.ProbeLength, cached.IndexWrite, cached.IndexLength, cached.PacksWrite))
            {
                return cached.Map;
            }
        }

        Dictionary<string, CosmeticCompatInfo> map = ReadAllUncached();

        lock (CacheLock)
        {
            _cache = (key.Item1, key.Item2, key.Item3, key.Item4, key.Item5, map);
        }

        return map;
    }

    private static Dictionary<string, CosmeticCompatInfo> ReadAllUncached()
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
            bool autoRepairRigs = ExperimentalAutoRepairRigs;
            string skeletonMode = CustomSkeletonMode;
            Dictionary<string, string> pieceTypes = LoadPieceTypes();
            foreach (var (itemId, facts) in geometry)
            {
                map[itemId] = Classify(facts, maxGeometry, pieceTypes.GetValueOrDefault(itemId), autoRepairRigs, skeletonMode);
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

    /// <summary>The synced catalog index — its items carry the piece type the
    /// face-bone scoping needs (same file the ApiServer's LivePersonaCatalog
    /// reads). Custom cosmetics are not in the index; their piece types come
    /// from the caller when one is known, and unknown/missing piece types never
    /// block on face bones (the filter only hides positive evidence).</summary>
    public static string IndexPath
        => Path.Combine(MojangAssetPaths.MojangAssetsDir, "persona-catalog", "index.json");

    private sealed class IndexFile
    {
        public List<IndexItem>? Items { get; set; }
    }

    private sealed class IndexItem
    {
        public string? Id { get; set; }
        public string? PieceType { get; set; }
    }

    private static Dictionary<string, string> LoadPieceTypes()
    {
        try
        {
            if (!File.Exists(IndexPath))
            {
                return [];
            }

            var file = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(IndexPath), JsonOptions);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (IndexItem? item in file?.Items ?? [])
            {
                if (!string.IsNullOrWhiteSpace(item?.Id) && !string.IsNullOrWhiteSpace(item.PieceType))
                {
                    map[item.Id] = item.PieceType;
                }
            }

            return map;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // a corrupt index must never break the display — face bones then
            // never scope to anything and no badge turns risky on them
            return [];
        }
    }

    private static CosmeticCompatInfo Classify(GeometryFacts? facts, Version maxGeometry, string? pieceType, bool autoRepairRigs, string skeletonMode = "off")
    {
        if (facts is null)
        {
            return Unknown;
        }

        // The format_version being missing/unparsable no longer short-circuits
        // the classification: the ApiServer's gate (PersonaPieceTypes
        // .EvaluateCosmetic) evaluates effects/bones/bounds even without a
        // parsable version — the old early-unknown return swallowed the bones
        // signal for exactly the pieces the repairer needed to see. Same rule
        // here so the badges match what the gate enforces.
        bool versionKnown = facts.FormatVersion is not null
            && TryParseFormatVersion(facts.FormatVersion, out Version _);
        string versionText = versionKnown
            ? $"geometry format_version {facts.FormatVersion}"
            : "no parsable format_version";

        if (versionKnown && TryParseFormatVersion(facts.FormatVersion, out Version version) && version >= maxGeometry)
        {
            return new CosmeticCompatInfo(
                CosmeticCompatKind.Risky,
                $"{versionText} — at or past the {maxGeometry} cap (UV rotation, transform pivots the frozen client cannot render)");
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

        // a declared texture no zip entry carries — the piece imports and
        // renders UNTEXTURED ("losing textures"); recorded since schema v6
        if (facts.HasTextureBindingIssue == true)
        {
            effects.Add("declared texture missing from the zip (renders untextured)");
        }

        // face bones mark the face-accessory rig (MCPE-165232) — risky only for
        // face accessories; hats/hoods legitimately carry hat/head bones
        if (facts.HasFaceBones == true && pieceType is "persona_face_accessory")
        {
            effects.Add("face bones (face-accessory rig 1.19.50/1.20.10, MCPE-165232)");
        }

        // custom rig bones + oversized bounds — displaced/stretched renders on
        // the frozen client; scoped to the head family exactly like the gate
        // (PersonaPieceTypes.IsHeadFamilyPieceType): hair exempt (ponytails),
        // back/outerwear exempt (wings exceed bounds). Keep in sync with the
        // gate's thresholds. With the experimental auto-repair on, custom
        // bones alone no longer make the piece plain risky — it becomes
        // auto-repairable below; oversized bounds or any other effect signal
        // still mean Risky (the repair fixes bones only).
        bool repairableBones = false;
        bool stripMode = skeletonMode is "strip" or "flatOnly";
        if (pieceType is "persona_head" or "persona_eyes" or "persona_mouth" or "persona_face_accessory")
        {
            if (facts.HasNonstandardBones == true)
            {
                // a serve-time transform is active under either knob: the
                // experimental re-parent repair, or the custom-skeleton strip
                if (autoRepairRigs || stripMode)
                {
                    repairableBones = true;
                }
                else
                {
                    effects.Add("custom rig bones (the client binds only the standard player bones)");
                }
            }

            if (facts.MaxVisibleBoundsWidth > 2.0f || facts.MaxVisibleBoundsHeight > 3.0f)
            {
                effects.Add($"oversized bounds ({facts.MaxVisibleBoundsWidth}x{facts.MaxVisibleBoundsHeight})");
            }
        }

        if (effects.Count > 0)
        {
            return new CosmeticCompatInfo(
                CosmeticCompatKind.Risky,
                $"{versionText} but uses {string.Join(", ", effects)}");
        }

        if (repairableBones)
        {
            return new CosmeticCompatInfo(
                CosmeticCompatKind.AutoRepairable,
                stripMode
                    ? $"{versionText} — custom rig bones are the only breakage signal and the experimental skeleton strip removes them at serve time (packs.json customSkeletonMode)"
                    : $"{versionText} — custom rig bones are the only breakage signal and the experimental auto-rig-repair re-parents them onto \"head\" at serve time");
        }

        return new CosmeticCompatInfo(
            CosmeticCompatKind.Supported,
            versionKnown
                ? $"geometry format_version {facts.FormatVersion}, no animation/effect signals"
                : "no parsable format_version but no risk signal either (bones/bounds/effects still checked)");
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
