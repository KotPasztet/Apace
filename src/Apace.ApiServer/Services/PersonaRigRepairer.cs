using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace Apace.ApiServer.Services;

// EXPERIMENTAL serve-time rig fixer for 3D-broken cosmetics (packs.json
// "experimentalAutoRepairRigs" re-parent, DEFAULT OFF — PersonaPackGate; plus
// the "customSkeletonMode" knob: "strip" / "flatOnly"). Pieces flagged risky
// ONLY because of non-standard ("custom") bones — whole-head replacements with
// invented bone trees ("Squid head", "Jelly UFO head") — render DISPLACED on
// the frozen MCE client: it binds only the standard player rig bones, and
// every bone outside that set leaves its cubes at raw local coordinates, so
// the model scatters. Two transforms, both rewrite the piece binary at serve
// time:
//
//  - REPAIR (re-parent, "experimentalAutoRepairRigs"): every custom bone that
//    is the ROOT of a custom subtree is re-parented onto the standard "head"
//    bone; deeper custom-bone → custom-bone links stay untouched, so each
//    subtree hangs together under head and the client can bind the whole
//    chain.
//  - STRIP (packs.json "customSkeletonMode": "strip" or "flatOnly"): every
//    custom bone is REMOVED and its cubes merge into the nearest standard
//    ancestor bone — the served geometry only contains standard bones, so it
//    renders rigidly on the standard rig and nothing can fly apart. In
//    Flat-only mode the offer additionally HIDES every custom-skeleton piece
//    (PersonaPieceTypes hideCustomSkeleton) so only "flat" cosmetics are
//    listed; the strip still applies to binaries a cached client requests.
//
// Why moving cubes between bones cannot move any VERTICES: the codebase never
// parses bone pivots (the compat probe records bone NAMES, format versions and
// visible bounds — LivePersonaCatalog.ScanRigShape), so nothing here can
// contradict the format semantics it was verified against: in Bedrock geometry
// (format_version >= 1.12.0) bone "pivot", cube "origin" and cube "pivot" are
// ABSOLUTE model-space coordinates, not relative to the parent bone. On the
// frozen client an unbindable custom bone ignores its own pivot/rotation
// entirely — its cubes render at their raw absolute coordinates, which is the
// observed displaced-but-stationary rest pose. Merging those cubes into the
// standard ancestor (keeping every coordinate verbatim, dropping only the dead
// bone nodes) therefore reproduces the exact rest pose AND makes the cubes
// follow the ancestor the client actually binds.
//
// Container handling mirrors the compat probe (LivePersonaCatalog
// .CollectZipEntries / InspectCompatEntries) exactly: piece binaries are an
// outer zip holding one nested ppack0.zip (flat custom zips are accepted too,
// and deeper nesting is walked), the nested layer's art files are per-file
// encrypted through the key map in its contents.json (PersonaContentCrypto), a
// geometry file is detected by CONTENT (any .json holding a minecraft:geometry
// block, a bones array or a legacy geometry.<name> wrapper — lenient parse,
// comments and trailing commas are common in Bedrock files). Entries with a
// key in the layer's map are decrypted before the patch and re-encrypted with
// the SAME key afterwards, so the client's contents.json stays valid; every
// other entry (textures, manifests, ciphertext without a usable key) is copied
// byte-for-byte into the repacked container with its original name and
// stored-vs-deflated compression.
//
// Failure contract: an unparsable/binary-only zip, or one with no repairable
// bones, serves the ORIGINAL bytes untouched; any exception is logged as a
// warning and swallowed — this runs on the binary download path and must never
// break a request (a 404/500 here strands the client on its loading screen).
public static class PersonaRigRepairer
{
    /// <summary>The short cache-identity marker worked into the ETags of
    /// rewritten pieces — a rewritten piece's identity differs from the
    /// original bytes, so clients re-download after the admin flips a knob.
    /// Bump it if a transform itself ever changes meaningfully ("r3": the
    /// transform also blanks animations (geometry "animations" blocks and
    /// *.animation.json payloads) and rotated custom subtrees are stripped
    /// rigid instead of re-parented, and the scope extends to every piece
    /// type — non-head pieces strip into their nearest standard ancestor).</summary>
    public const string RepairMarker = "r3";

    /// <summary>The marker the server currently serves for rewritten pieces —
    /// mode-aware ("r2-strip" while a strip mode is active, "r2" for the plain
    /// re-parent repair) so flipping between the knobs is also an identity
    /// change the client can see.</summary>
    public static string CurrentServeMarker
        => PersonaPackGate.CustomSkeletonMode switch
        {
            PersonaPackGate.CustomSkeletonModes.Strip => $"{RepairMarker}-strip",
            PersonaPackGate.CustomSkeletonModes.FlatOnly => $"{RepairMarker}-flat",
            _ => RepairMarker,
        };

    // The bone custom subtree roots are re-parented onto (repair), and the
    // fallback hoist target for stripped cubes whose whole bone chain is
    // custom (strip). The head slot is where the repair applies (the gate
    // scopes custom bones to the head family — PersonaPieceTypes
    // .IsHeadFamilyPieceType), and "head" is one of the standard player bones
    // the frozen client provably binds (it shipped with hundreds of
    // hat/head-rigged pieces).
    private const string RepairParentBone = "head";

    // Outer zip + ppack0.zip + (exotic) deeper layers — mirrors how far the
    // compat probe walks the container.
    private const int MaxLayers = 4;

    // Per-entry size cap, same as the probe's MaxCompatEntryBytes: bigger
    // entries (large textures) are copied verbatim, never parsed.
    private const long MaxEntryBytes = 2 * 1024 * 1024;

    // Repaired binaries are cheap to rebuild and few in number (only head
    // pieces whose sole risk signal is custom bones) — a small cache keyed by
    // "<mode>|<item id>" with the SOURCE file stamp validates them; beyond the
    // cap the cache clears rather than grows.
    private const int MaxCachedPieces = 64;

    private static readonly ConcurrentDictionary<string, (long Stamp, RepairedBinary Binary)> RepairCache = new(StringComparer.OrdinalIgnoreCase);

    // One line per repaired piece per server run (the binary endpoint is hit
    // on every client cache miss, not once).
    private static readonly HashSet<string> RepairedLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One repaired piece binary: the rewritten container bytes and
    /// the stable ETag to serve with them.</summary>
    public sealed record RepairedBinary(byte[] Bytes, string ETag);

    /// <summary>
    /// True when the piece's geometry should be REWRITTEN at serve time with
    /// the active transform. The gate (PersonaPieceTypes.EvaluateCosmetic with
    /// allowRigRepair) scopes it now: head-family pieces with custom bones
    /// (re-parent / strip), NON-head pieces with custom bones (rigid strip —
    /// they used to serve raw and glitch) and ANY piece whose only risk signal
    /// is animations (the rewriter blanks them) — every flavor reports
    /// RepairedRig. Pieces the verdict still blocks (format cap, particles,
    /// render controllers, missing texture binding, oversized head bounds,
    /// explicit overrides) stay hidden — the transform fixes bones and
    /// animations only, so it must not touch those.
    /// </summary>
    public static bool IsRepairCandidate(string itemId, string? pieceType)
    {
        if (!PersonaPackGate.ServeTimeTransformActive)
        {
            return false;
        }

        // hideCustomSkeleton: false — the serve-time caller strips instead of
        // hiding, whatever the offer-side Flat-only rule decided
        return PersonaPieceTypes.EvaluateCosmetic(itemId, pieceType, allowRigRepair: true) is { Supported: true, RepairedRig: true };
    }

    /// <summary>
    /// The repaired container for a piece binary, or null when the piece is
    /// not a repair candidate, its content could not be parsed, or nothing
    /// was repairable — in every null case the caller serves the original
    /// bytes. Successful repairs are cached per item id (validated against the
    /// source file stamp, so a re-mirrored or re-uploaded binary repairs
    /// fresh) and logged once per piece.
    /// </summary>
    public static RepairedBinary? TryRepair(string itemId, string? pieceType, string zipPath, Serilog.ILogger logger)
    {
        if (!IsRepairCandidate(itemId, pieceType))
        {
            LogSkipOnce(itemId, pieceType, logger);
            return null;
        }

        long stamp;
        byte[] original;
        try
        {
            var info = new FileInfo(zipPath);
            if (!info.Exists || info.Length <= 0)
            {
                return null;
            }

            stamp = SourceStamp(info);
            original = File.ReadAllBytes(zipPath);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Auto rig repair could not read {Item} from {Path} — serving the original binary", itemId, zipPath);
            return null;
        }

        // The transform identity rides in the cache key: flipping between the
        // repair and the strip knob rebuilds instead of serving stale output
        string cacheKey = $"{PersonaPackGate.CustomSkeletonMode}|{itemId}";
        if (RepairCache.TryGetValue(cacheKey, out (long Stamp, RepairedBinary Binary) cached) && cached.Stamp == stamp)
        {
            return cached.Binary;
        }

        // Head-family pieces re-parent onto "head" (they follow the head the
        // client animates); a strip mode forces the rigid transform for them
        // too. Every OTHER piece type strips: hoisting cubes into the nearest
        // standard ancestor preserves the rest pose exactly and cannot apply
        // rotations around a wrong pivot — the safe transform for bodies,
        // limbs and accessories.
        // FACE ACCESSORIES always take the rigid strip (with face bones
        // strippable, below): the misplaced face rig bone (MCPE-165232) can
        // never stay in the served geometry, and a re-parent cannot fix it —
        // the bone itself is what the client cannot bind, so it must be
        // removed/renamed, which is the strip's job.
        bool stripFaceBones = pieceType is "persona_face_accessory";
        bool strip = stripFaceBones
            || PersonaPackGate.CustomSkeletonMode != PersonaPackGate.CustomSkeletonModes.Off
            || !PersonaPieceTypes.IsHeadFamilyPieceType(pieceType);
        var transformedBones = new List<string>();
        var strippedAnimations = new List<string>();
        byte[]? transformed = null;
        try
        {
            transformed = RewriteLayer(original, fileKeys: null, firstNestedReadsOwnKeys: true, transformedBones, strippedAnimations, MaxLayers, strip, stripFaceBones);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Auto rig repair could not rewrite {Item} ({Path}) — serving the original binary", itemId, zipPath);
        }

        if (transformed is null || (transformedBones.Count == 0 && strippedAnimations.Count == 0))
        {
            return null; // unparsable / binary-only / nothing repairable — skip silently
        }

        var binary = new RepairedBinary(transformed, BuildEtag(itemId, stamp));
        if (RepairCache.Count >= MaxCachedPieces)
        {
            RepairCache.Clear();
        }

        RepairCache[cacheKey] = (stamp, binary);
        LogTransformedOnce(itemId, transformedBones, strippedAnimations, strip);
        return binary;
    }

    /// <summary>The stable serve-time ETag for a rewritten piece: the mode
    /// marker (CurrentServeMarker) plus an FNV-1a fingerprint of the SOURCE
    /// file stamp (mtime + size, folded without GetHashCode — that is
    /// process-randomized and would break restart stability) plus the item id.
    /// Deterministic, so it survives restarts unchanged while the toggle state
    /// and the source bytes are unchanged; flipping a knob off stops the
    /// marker from being served at all, which is the identity change that
    /// makes clients re-fetch.</summary>
    private static string BuildEtag(string itemId, long stamp)
        => $"\"{CurrentServeMarker}-{Fnv1a(BitConverter.GetBytes(stamp)):x8}-{itemId}\"";

    // mtime + size folded into one identity: a re-mirrored or re-uploaded
    // binary produces a different stamp (fresh repair), an untouched file
    // keeps it identical across restarts.
    private static long SourceStamp(FileInfo info) => info.LastWriteTimeUtc.Ticks ^ (info.Length << 32);

    // FNV-1a — a stable, deterministic hash (unlike GetHashCode).
    private static ulong Fnv1a(byte[] data)
    {
        ulong hash = 14695981039346656037;
        foreach (byte value in data)
        {
            hash = (hash ^ value) * 1099511628211;
        }

        return hash;
    }

    private static void LogTransformedOnce(string itemId, List<string> bones, List<string> strippedAnimations, bool strip)
    {
        lock (RepairedLogged)
        {
            if (!RepairedLogged.Add(itemId))
            {
                return;
            }
        }

        string animationNote = strippedAnimations.Count > 0
            ? $" — animations blanked in {strippedAnimations.Count} file(s)"
            : "";
        if (strip)
        {
            Log.Information("Skeleton strip served {Item}: removed custom bone(s) {Bones} — their cubes merged into the nearest standard bone (packs.json customSkeletonMode / non-head rigid strip){AnimationNote}",
                itemId, bones.Count > 0 ? string.Join(", ", bones) : "(none)", animationNote);
        }
        else
        {
            Log.Information("Auto rig repair served {Item}: re-parented custom bone(s) {Bones} onto \"{Bone}\" (packs.json experimentalAutoRepairRigs){AnimationNote}",
                itemId, string.Join(", ", bones), RepairParentBone, animationNote);
        }
    }

    // One line per UNREPAIRED piece that carries custom bones while the knob
    // is on — the "kompletnie nie działa naprawiacz" report was undiagnosable
    // because every skip path was silent. Deduplicated per item per run like
    // LogRepairedOnce.
    private static readonly HashSet<string> SkipsLogged = new(StringComparer.OrdinalIgnoreCase);

    private static void LogSkipOnce(string itemId, string? pieceType, Serilog.ILogger logger)
    {
        if (!PersonaPackGate.ServeTimeTransformActive)
        {
            return; // every knob is off — the rewriter is not supposed to do anything
        }

        if (!LivePersonaCatalog.TryGetGeometryCompat(itemId, out _, out _, out bool? hasAnimations, out _, out _, out _, out _, out _, out bool? hasNonstandardBones, out _)
            || (hasNonstandardBones != true && hasAnimations != true))
        {
            return; // no custom-bone/animation facts — nothing the rewriter could act on anyway
        }

        lock (SkipsLogged)
        {
            if (!SkipsLogged.Add(itemId))
            {
                return;
            }
        }

        string reason = !PersonaPieceTypes.IsHeadFamilyPieceType(pieceType)
            ? $"the piece type \"{pieceType ?? "?"}\" is not head-family (the rewriter scopes to head/eyes/mouth/face-accessory)"
            : PersonaPieceTypes.EvaluateCosmetic(itemId, pieceType, allowRigRepair: true) is { Supported: false } blocked
                ? $"the compat gate still blocks it: {blocked.Reason}"
                : "the verdict passed but the container produced no rewrite (unparsable content or nothing repairable)";
        Log.Information("Rig rewriter ON but {Item} was served UNCHANGED: {Reason}", itemId, reason);
    }

    // ---------------------------------------------------------------------------
    // Container rewrite
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds one zip layer entry-by-entry, rewriting the geometry JSONs it
    /// can parse and copying everything else verbatim. <paramref name="fileKeys"/>
    /// is the per-file key map DECRYPTING this layer's entries (null = raw
    /// layer); <paramref name="firstNestedReadsOwnKeys"/> mirrors the probe's
    /// key-map rule — the first nested level resolves its keys from its own
    /// contents.json, deeper levels inherit their parent's map. Returns the
    /// new container bytes; throws only on structural zip failures (the caller
    /// catches and serves the original).
    /// </summary>
    private static byte[] RewriteLayer(byte[] zipBytes, Dictionary<string, string>? fileKeys, bool firstNestedReadsOwnKeys, List<string> transformedBones, List<string> strippedAnimations, int depth, bool strip, bool stripFaceBones)
    {
        using var sourceStream = new MemoryStream(zipBytes);
        using var sourceArchive = new ZipArchive(sourceStream, ZipArchiveMode.Read, leaveOpen: false);
        using var targetStream = new MemoryStream();
        using (var targetArchive = new ZipArchive(targetStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in sourceArchive.Entries)
            {
                byte[] originalBytes = ReadEntryBytes(entry);
                byte[]? payload = null;
                bool isZip = entry.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                bool isAnimationFile = entry.Name.EndsWith(".animation.json", StringComparison.OrdinalIgnoreCase);
                bool isJson = entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !isAnimationFile;
                if ((isZip || isJson || isAnimationFile) && entry.Length is > 0 and <= MaxEntryBytes)
                {
                    if (isZip)
                    {
                        if (depth > 0)
                        {
                            // the nested layer: its own contents.json carries its per-file
                            // keys (null when it has none readable — scanned raw); deeper
                            // layers inherit, exactly like the probe's collector
                            Dictionary<string, string>? childKeys = firstNestedReadsOwnKeys
                                ? TryReadLayerKeys(originalBytes) ?? fileKeys
                                : fileKeys;
                            payload = RewriteLayer(originalBytes, childKeys, firstNestedReadsOwnKeys: false, transformedBones, strippedAnimations, depth - 1, strip, stripFaceBones);
                        }
                    }
                    else if (isAnimationFile)
                    {
                        // ANIMATION BLANK: an animation definition the frozen
                        // client mis-applies is the "glitchy" signal — the
                        // piece serves STATIC. The entry itself stays in the
                        // container (a manifest that names it keeps
                        // resolving) but its payload becomes an empty
                        // animation set; keyed entries re-encrypt with their
                        // original key so contents.json keeps validating.
                        payload = BlankAnimationEntry(originalBytes, fileKeys, entry, strippedAnimations);
                    }
                    else
                    {
                        payload = PatchJsonEntry(originalBytes, fileKeys, entry, transformedBones, strippedAnimations, strip, stripFaceBones);
                    }
                }

                // a rewritten entry always recompresses; a verbatim one keeps its
                // original stored-vs-deflated shape, and oversized entries (beyond
                // the parse cap — big textures) land here too, byte-for-byte
                byte[] content = payload ?? originalBytes;
                CompressionLevel level = payload is null ? LevelFor(entry) : CompressionLevel.Optimal;
                ZipArchiveEntry copy = targetArchive.CreateEntry(entry.FullName, level);
                copy.LastWriteTime = entry.LastWriteTime;
                using (Stream destination = copy.Open())
                {
                    destination.Write(content, 0, content.Length);
                }
            }
        }

        return targetStream.ToArray();
    }

    // The exact blank payload an *.animation.json entry gets: a well-formed
    // Bedrock animation file that defines no animations (the client parses it
    // and binds nothing).
    private static readonly byte[] BlankAnimationJson = Encoding.UTF8.GetBytes("{\"format_version\":\"1.8.0\",\"animations\":{}}");

    private static byte[]? BlankAnimationEntry(byte[] bytes, Dictionary<string, string>? fileKeys, ZipArchiveEntry entry, List<string> strippedAnimations)
    {
        strippedAnimations.Add(entry.Name);
        string? fileKey = null;
        if (fileKeys is not null
            && (fileKeys.TryGetValue(entry.Name, out fileKey)
                || fileKeys.TryGetValue(entry.FullName, out fileKey)))
        {
            if (PersonaContentCrypto.TryDecryptEntry(bytes, fileKey) is null)
            {
                strippedAnimations.Remove(entry.Name);
                return null; // keyed entry that does not decrypt — leave the ciphertext alone
            }

            // re-encrypt the blank payload with the SAME key (contents.json keeps validating)
            return PersonaContentCrypto.Encrypt(Encoding.ASCII.GetBytes(fileKey), BlankAnimationJson);
        }

        return BlankAnimationJson;
    }

    private static CompressionLevel LevelFor(ZipArchiveEntry entry)
        // practical "same compression as the original": stored entries (compressed
        // size == uncompressed size) stay stored, the rest recompresses deflated
        => entry.CompressedLength == entry.Length && entry.Length > 0 ? CompressionLevel.NoCompression : CompressionLevel.Optimal;

    /// <summary>Patches ONE .json entry: decrypts it when the layer's key map
    /// holds a key for it, applies the active bone transform (strip or
    /// re-parent) when it holds a geometry structure, and re-encrypts with the
    /// same key when it was decrypted (the client's contents.json keeps
    /// validating). Null = leave the entry byte-identical (not a geometry,
    /// nothing repairable, or the key did not decrypt — foreign/corrupt
    /// content is never rewritten).</summary>
    private static byte[]? PatchJsonEntry(byte[] bytes, Dictionary<string, string>? fileKeys, ZipArchiveEntry entry, List<string> transformedBones, List<string> strippedAnimations, bool strip, bool stripFaceBones)
    {
        string? fileKey = null;
        if (fileKeys is not null
            && (fileKeys.TryGetValue(entry.Name, out fileKey)
                || fileKeys.TryGetValue(entry.FullName, out fileKey)))
        {
            if (PersonaContentCrypto.TryDecryptEntry(bytes, fileKey) is not { } decrypted)
            {
                return null; // keyed entry that does not decrypt — leave the ciphertext alone
            }

            bytes = decrypted;
        }

        byte[]? patched = strip
            ? StripGeometry(bytes, transformedBones, strippedAnimations, stripFaceBones)
            : PatchGeometry(bytes, transformedBones, strippedAnimations);
        if (patched is null)
        {
            return null;
        }

        return fileKey is not null ? PersonaContentCrypto.Encrypt(Encoding.ASCII.GetBytes(fileKey), patched) : patched;
    }

    /// <summary>Reads a nested layer's contents.json key map (raw — the
    /// contents.json itself is never per-file encrypted). Null when the layer
    /// carries none or it does not parse, which the probe treats as a raw
    /// layer.</summary>
    private static Dictionary<string, string>? TryReadLayerKeys(byte[] nestedZipBytes)
    {
        try
        {
            using var stream = new MemoryStream(nestedZipBytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            ZipArchiveEntry? contents = archive.GetEntry("contents.json");
            return contents is null ? null : PersonaContentCrypto.TryReadContentsKeys(ReadEntryBytes(contents));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // ---------------------------------------------------------------------------
    // Geometry patch (mirrors the probe's BoneLists detection on JsonNode)
    // ---------------------------------------------------------------------------

    // Bedrock resource JSON is lenient in the wild: // comments and trailing
    // commas are common — parse leniently, exactly like the compat probe.
    private static readonly JsonDocumentOptions LenientJson = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Re-parents the ROOT of every custom bone subtree onto the
    /// standard head bone — UNLESS the subtree carries bone rotations: a
    /// bindable bone's rotation now APPLIES around its pivot (before the
    /// repair the frozen client ignored it entirely), so re-parenting a posed
    /// subtree moves/scatters it — the reported "całkowicie psuje geometrię".
    /// A rotated subtree takes the rigid path instead: its custom bones strip
    /// into the nearest bindable ancestor (rest pose preserved exactly, the
    /// piece just follows that ancestor's animation). Returns null when the
    /// content is not a geometry structure or nothing changed — the entry
    /// then stays byte-identical. Root-level "animations" blocks are removed
    /// (ANIMATION BLANK — the served piece is static).
    /// Bone-name classification uses the probe's exact sets
    /// (LivePersonaCatalog.StandardRigBoneNames / FaceBoneNames, shared here so
    /// the two can never drift); a bone counts as a custom SUBTREE ROOT when
    /// its parent is absent or a standard/face bone — deeper custom → custom
    /// links stay untouched. Property order and number formatting are
    /// preserved by the JsonNode round-trip; only patched geometry files are
    /// re-serialized at all.</summary>
    private static byte[]? PatchGeometry(byte[] plainJson, List<string> repairedBones, List<string> strippedAnimations)
    {
        string text = Encoding.UTF8.GetString(plainJson);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..]; // strip a UTF-8 BOM — the strict parser throws on it
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text, documentOptions: LenientJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is not JsonObject root || !HoldsGeometry(root))
        {
            return null;
        }

        int changed = 0;
        bool animationsRemoved = RemoveAnimations(root);
        foreach (JsonArray bones in BoneLists(root))
        {
            if (SubtreeHasRotations(bones))
            {
                // a posed custom subtree would move when the client starts
                // applying its rotations — strip it rigid instead (rest pose
                // preserved, cubes follow the nearest bindable ancestor).
                // stripFaceBones is false here by construction: TryRepair
                // forces the strip transform (which owns face-bone handling)
                // for face accessories, so the re-parent path never sees one.
                changed += StripBoneList(bones, repairedBones, stripFaceBones: false);
            }
            else
            {
                changed += ReparentSubtreeRoots(bones, repairedBones);
            }
        }

        if (changed == 0)
        {
            return animationsRemoved ? Encoding.UTF8.GetBytes(root.ToJsonString()) : null;
        }

        if (animationsRemoved)
        {
            strippedAnimations.Add("(geometry animations block)");
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    // Root-level "animations" — the model-embedded animation definitions the
    // frozen client mis-applies. Removing the key turns the piece static.
    private static bool RemoveAnimations(JsonObject root)
    {
        if (!root.ContainsKey("animations"))
        {
            return false;
        }

        root.Remove("animations");
        return true;
    }

    // True when any bone in a custom subtree under this list carries a
    // "rotation" — walking parent links through byName so grandchildren count.
    private static bool SubtreeHasRotations(JsonArray bones)
    {
        var byName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? node in bones)
        {
            if (node is JsonObject bone && GetBoneName(bone) is { Length: > 0 } name)
            {
                byName.TryAdd(name, bone);
            }
        }

        foreach (JsonNode? node in bones)
        {
            if (node is not JsonObject bone || GetBoneName(bone) is not { Length: > 0 } boneName)
            {
                continue;
            }

            // face bones are bindable here: the re-parent path never handles a
            // face accessory (TryRepair forces the strip for them), so the
            // face-attachment set keeps counting as bindable
            if (IsCustomBone(boneName, bone, stripFaceBones: false) || HasRotationProperty(bone))
            {
                string? parent = GetParentName(bone);
                bool parentIsCustom = parent is not null
                    && byName.TryGetValue(parent, out JsonObject? parentBone)
                    && IsCustomBone(parent, parentBone, stripFaceBones: false);
                if (parentIsCustom)
                {
                    continue; // reached through its custom parent's check
                }

                // walk the subtree below (and including) this root
                if (SubtreeContainsRotation(bone, bones, new HashSet<JsonObject>(ReferenceEqualityComparer.Instance)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SubtreeContainsRotation(JsonObject bone, JsonArray bones, HashSet<JsonObject> visited)
    {
        if (!visited.Add(bone))
        {
            return false;
        }

        if (HasRotationProperty(bone))
        {
            return true;
        }

        string? boneName = GetBoneName(bone);
        if (boneName is null)
        {
            return false;
        }

        foreach (JsonNode? node in bones)
        {
            if (node is JsonObject child
                && string.Equals(GetParentName(child), boneName, StringComparison.OrdinalIgnoreCase)
                && SubtreeContainsRotation(child, bones, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasRotationProperty(JsonObject bone)
    {
        JsonNode? rotation = bone["rotation"];
        if (rotation is null)
        {
            return false;
        }

        if (rotation is JsonArray components)
        {
            foreach (JsonNode? component in components)
            {
                if (RotationValueNonZero(component))
                {
                    return true;
                }
            }

            return false;
        }

        return RotationValueNonZero(rotation); // scalar — or a molang expression, which animates too
    }

    private static bool RotationValueNonZero(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return false;
        }

        if (value.TryGetValue(out double scalar))
        {
            return Math.Abs(scalar) > 0.001;
        }

        // non-numeric string = a molang expression — a live rotation signal
        return value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) && text.Trim() != "0";
    }

    // The extracted re-parent pass: every custom subtree ROOT (parent absent or
    // bindable) hangs onto the standard head bone; deeper custom → custom links
    // stay untouched. Callers run it ONLY for rotation-free bone lists — a
    // posed subtree takes the rigid strip path instead (see SubtreeHasRotations).
    private static int ReparentSubtreeRoots(JsonArray bones, List<string> repairedBones)
    {
        int changed = 0;
        foreach (JsonNode? boneNode in bones)
        {
            if (boneNode is not JsonObject bone
                || bone["name"] is not JsonValue nameValue
                || !nameValue.TryGetValue(out string? boneName)
                || string.IsNullOrEmpty(boneName))
            {
                continue;
            }

            // custom bone = outside the standard player rig AND outside the
            // face-attachment set (hat/head bones the client renders fine)
            if (LivePersonaCatalog.StandardRigBoneNames.Contains(boneName)
                || LivePersonaCatalog.FaceBoneNames.Contains(boneName))
            {
                continue;
            }

            string? parent = bone["parent"] is JsonValue parentValue && parentValue.TryGetValue(out string? parentName)
                ? parentName
                : null;

            // subtree root: no parent, or a parent the client can already bind
            bool parentIsBindable = string.IsNullOrEmpty(parent)
                || LivePersonaCatalog.StandardRigBoneNames.Contains(parent)
                || LivePersonaCatalog.FaceBoneNames.Contains(parent);
            if (!parentIsBindable
                || string.Equals(parent, RepairParentBone, StringComparison.OrdinalIgnoreCase))
            {
                continue; // deeper custom → custom link, or already on head — untouched
            }

            bone["parent"] = RepairParentBone;
            changed++;
            repairedBones.Add(boneName);
        }

        return changed;
    }

    /// <summary>STRIP: removes every custom bone from the geometry and merges
    /// its cubes into the nearest standard/face ancestor bone, so the served
    /// skeleton contains ONLY bones the frozen client binds and the piece
    /// renders rigidly (nothing can fly apart). Root-level "animations"
    /// blocks are removed too (ANIMATION BLANK — the served piece is static).
    /// With <paramref name="stripFaceBones"/> (face-accessory pieces) the
    /// "face" rig bone is strippable too — it is the bone the frozen client
    /// misplaces (MCPE-165232), so it must leave the served skeleton exactly
    /// like a custom bone; "hat"/"head" stay bindable (standard player bones).
    /// Returns null when the content is not a geometry structure and no
    /// animations were removed — the entry then stays byte-identical.
    ///
    /// Safety: cube "origin"/"pivot" are ABSOLUTE model-space coordinates, so
    /// moving a cube from an unbindable bone into a bindable ancestor changes
    /// no vertex position at rest (the frozen client ignored the dead bone's
    /// pivot/rotation entirely — that was the displaced rest pose) and the
    /// cubes start following the ancestor the client actually binds. The
    /// stripped bones' locators are dropped with them ("almost completely" by
    /// design); a custom bone whose WHOLE chain has no standard ancestor above
    /// it keeps its own cubes attached to itself when the bones list carries
    /// no bindable fallback bone at all — its custom children are still
    /// stripped and their cubes bubble up to it.</summary>
    private static byte[]? StripGeometry(byte[] plainJson, List<string> strippedBones, List<string> strippedAnimations, bool stripFaceBones)
    {
        string text = Encoding.UTF8.GetString(plainJson);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..]; // strip a UTF-8 BOM — the strict parser throws on it
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text, documentOptions: LenientJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is not JsonObject root || !HoldsGeometry(root))
        {
            return null;
        }

        int changed = 0;
        bool animationsRemoved = RemoveAnimations(root);
        foreach (JsonArray bones in BoneLists(root))
        {
            changed += StripBoneList(bones, strippedBones, stripFaceBones);
        }

        if (changed == 0)
        {
            return animationsRemoved ? Encoding.UTF8.GetBytes(root.ToJsonString()) : null;
        }

        if (animationsRemoved)
        {
            strippedAnimations.Add("(geometry animations block)");
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static int StripBoneList(JsonArray bones, List<string> strippedBones, bool stripFaceBones)
    {
        // name → bone, so parent links resolve (first occurrence wins for
        // duplicate names)
        var byName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? node in bones)
        {
            if (node is JsonObject bone
                && GetBoneName(bone) is { Length: > 0 } name)
            {
                byName.TryAdd(name, bone);
            }
        }

        var visited = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        int changed = 0;

        foreach (JsonNode? node in bones.ToList()) // snapshot — bones get removed inside
        {
            if (node is not JsonObject bone
                || visited.Contains(bone)
                || GetBoneName(bone) is not { Length: > 0 } boneName)
            {
                continue;
            }

            string? parentName = GetParentName(bone);
            bool parentIsCustom = parentName is not null
                && byName.TryGetValue(parentName, out JsonObject? parentBone)
                && IsCustomBone(parentName, parentBone, stripFaceBones);
            if (parentIsCustom)
            {
                continue; // reached through its custom parent's recursion
            }

            // top of a subtree: recursion strips the custom descendants and
            // bubbles their cubes up; a null return means the bone stayed
            List<JsonNode>? hoisted = ProcessBone(bone, bones, visited, strippedBones, ref changed, stripFaceBones);

            if (hoisted is { Count: > 0 })
            {
                // the recursion root itself was custom — merge everything into
                // its bindable parent, else "head", else any standard bone
                JsonObject? target = null;
                if (parentName is not null
                    && byName.TryGetValue(parentName, out JsonObject? parent)
                    && !IsCustomBone(parentName, parent, stripFaceBones))
                {
                    target = parent;
                }

                target ??= FindFallbackBone(bones);
                if (target is not null)
                {
                    AttachCubes(target, hoisted);
                    bones.Remove(bone);
                }
                else
                {
                    // no bindable bone anywhere in this list — keep the root
                    // (its custom children were still stripped and their cubes
                    // bubble up to it, which is the client's rest pose anyway)
                    AttachCubes(bone, hoisted);
                    bool faceBoneRenamed = stripFaceBones
                        && !LivePersonaCatalog.StandardRigBoneNames.Contains(boneName)
                        && LivePersonaCatalog.FaceBoneNames.Contains(boneName);
                    if (faceBoneRenamed)
                    {
                        // a lone FACE bone cannot stay (its name is the exact
                        // misplaced rig the gate hides, MCPE-165232) and there
                        // is no bindable bone to merge it into — rename it onto
                        // the standard head bone instead: cube origin/pivot are
                        // absolute model-space, so the rest pose is untouched
                        // and the piece now binds like the head-rigged pieces
                        // the client provably renders (it shipped with hundreds)
                        bone["name"] = RepairParentBone;
                    }
                    else
                    {
                        strippedBones.RemoveAll(candidate => string.Equals(candidate, boneName, StringComparison.Ordinal));
                    }
                }
            }

            visited.Add(bone);
        }

        return changed;
    }

    // Post-order strip of one bone subtree. Returns the cubes to bubble up to
    // the nearest bindable ancestor when the bone itself was custom (null when
    // the bone stayed — its own cubes plus everything its stripped children
    // hoisted are re-attached in place).
    private static List<JsonNode>? ProcessBone(JsonObject bone, JsonArray bones, HashSet<JsonObject> visited, List<string> strippedBones, ref int changed, bool stripFaceBones)
    {
        visited.Add(bone);
        string boneName = GetBoneName(bone)!;
        bool custom = IsCustomBone(boneName, bone, stripFaceBones);

        List<JsonNode> hoist = [];
        List<JsonNode> own = DetachCubes(bone);

        // recurse into the bones parented to this one (snapshot — they get
        // removed when stripped)
        foreach (JsonNode? childNode in bones.ToList())
        {
            if (childNode is not JsonObject child
                || visited.Contains(child)
                || GetBoneName(child) is not { Length: > 0 } childName
                || !string.Equals(GetParentName(child), boneName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<JsonNode>? childHoist = ProcessBone(child, bones, visited, strippedBones, ref changed, stripFaceBones);
            if (childHoist is not null)
            {
                hoist.AddRange(childHoist);
                bones.Remove(child);
            }
        }

        if (custom)
        {
            strippedBones.Add(boneName);
            changed++;
            hoist.InsertRange(0, own);
            return hoist;
        }

        AttachCubes(bone, own);
        AttachCubes(bone, hoist);
        return null;
    }

    private static string? GetBoneName(JsonObject bone)
        => bone["name"] is JsonValue nameValue && nameValue.TryGetValue(out string? name) ? name : null;

    private static string? GetParentName(JsonObject bone)
        => bone["parent"] is JsonValue parentValue && parentValue.TryGetValue(out string? parent) ? parent : null;

    // custom bone = outside the standard player rig AND outside the
    // face-attachment set (hat/head bones the client renders fine) — the exact
    // sets the compat probe records bones with, so the strip never removes a
    // bone the client binds. For FACE-ACCESSORY pieces (stripFaceBones) the
    // "face" bone joins the strippable set: it is the face rig the frozen
    // client misplaces (MCPE-165232 — the compat gate only serves such pieces
    // rewritten), while "hat"/"head" stay bindable (standard player bones).
    private static bool IsCustomBone(string boneName, JsonObject bone, bool stripFaceBones)
        => !LivePersonaCatalog.StandardRigBoneNames.Contains(boneName)
            && (stripFaceBones || !LivePersonaCatalog.FaceBoneNames.Contains(boneName));

    private static JsonObject? FindFallbackBone(JsonArray bones)
    {
        JsonObject? firstStandard = null;
        foreach (JsonNode? node in bones)
        {
            if (node is not JsonObject bone
                || GetBoneName(bone) is not { Length: > 0 } name)
            {
                continue;
            }

            if (string.Equals(name, RepairParentBone, StringComparison.OrdinalIgnoreCase))
            {
                return bone; // "head" — the preferred hoist target for head-family pieces
            }

            if (firstStandard is null
                && LivePersonaCatalog.StandardRigBoneNames.Contains(name))
            {
                firstStandard = bone;
            }
        }

        return firstStandard;
    }

    // Detaches the bone's "cubes" member and returns its cube nodes
    // parentless, ready to attach elsewhere (a JsonNode can only have one
    // parent — re-adding a still-attached node throws).
    private static List<JsonNode> DetachCubes(JsonObject bone)
    {
        var cubes = new List<JsonNode>();
        if (bone["cubes"] is JsonArray array)
        {
            foreach (JsonNode? cube in array.ToList())
            {
                if (cube is not null)
                {
                    array.Remove(cube);
                    cubes.Add(cube);
                }
            }

            bone.Remove("cubes");
        }

        return cubes;
    }

    private static void AttachCubes(JsonObject bone, List<JsonNode> cubes)
    {
        if (cubes.Count == 0)
        {
            return;
        }

        if (bone["cubes"] is not JsonArray array)
        {
            array = [];
            bone["cubes"] = array;
        }

        foreach (JsonNode cube in cubes)
        {
            array.Add(cube);
        }
    }

    // Geometry detection by CONTENT, same as the probe: a minecraft:geometry
    // block, a root-level bones array, or a legacy geometry.<name> wrapper —
    // whatever the file is called.
    private static bool HoldsGeometry(JsonObject root)
        => root.ContainsKey("minecraft:geometry")
            || BoneLists(root).Any();

    // The bone lists a geometry can carry: the modern minecraft:geometry
    // entries, a root-level "bones" array and the legacy "geometry.<name>"
    // wrapper objects — whichever shape the file uses (mirrors
    // LivePersonaCatalog.InspectCompatEntries.BoneLists).
    private static IEnumerable<JsonArray> BoneLists(JsonObject root)
    {
        if (root["bones"] is JsonArray rootBones)
        {
            yield return rootBones;
        }

        if (root["minecraft:geometry"] is JsonArray geometries)
        {
            foreach (JsonNode? geometry in geometries)
            {
                if (geometry is JsonObject geometryObject && geometryObject["bones"] is JsonArray geometryBones)
                {
                    yield return geometryBones;
                }
            }
        }

        foreach (KeyValuePair<string, JsonNode?> property in root)
        {
            if (property.Value is JsonObject wrapper && wrapper["bones"] is JsonArray wrappedBones)
            {
                yield return wrappedBones;
            }
        }
    }
}
