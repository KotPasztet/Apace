using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Apace.ApiServer.Services;

// Builds the placeholder piece binary served when a requested persona zip is
// unavailable (probed dead, never mirrored, not entitled for a live fetch).
// The frozen Minecraft Earth client RETRIES a failed piece download until its
// own timeout — a 404 leaves the player stuck on "loading" — so the download
// must SUCCEED instead: the placeholder is a complete, valid piece zip in the
// proven-working shape (scripts/build_apace_piece.py) whose geometry is the
// proven cape chain — a visible gray block on the player's back. VISIBLE ON
// PURPOSE: the first placeholder (empty bones, transparent texture) imported
// fine and rendered nothing, so every equip looked like it did nothing — a
// player must SEE the placeholder apply. Per-type rigs were tried and HANG
// the client's import (see BuildGeometry); the cape chain is the only shape
// this client provably accepts.
//
// Deterministic by construction: one piece uuid always yields the same bytes
// (fixed timestamps, uuid-derived file keys), so a client's cached copy of the
// placeholder stays consistent across rebuilds.
public static class PersonaStubBinary
{
    private const string StubName = "apace_stub";

    // 64×64 solid light-gray PNG — the placeholder texture (visible). Sized
    // like the real pieces: the proven-importable packs ship 128×128+ square
    // power-of-two textures and the smallest proven-working one is 128×128, so
    // a 1×1 texture was suspected of failing the client's import silently.
    private const string PlaceholderPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAATElEQVR42u3PMQ0AAAwDoPrXU4GVsHsJOCB9LgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgICAgIClwHoScHw0xgO1AAAAABJRU5ErkJggg==";

    // The manifest header/module version the client re-imports on ([3,0,2] —
    // same reasoning as the cape builder: a bump forces the client to use the
    // fresh pack instead of a cached copy of a previous one; bumped when the
    // geometry changed to the proven cape chain, since clients that already
    // imported an earlier placeholder would otherwise keep its cache).
    private static readonly int[] BuildVersion = [3, 0, 2];

    // fixed timestamps like the real pieces — keeps builds deterministic
    private static readonly DateTimeOffset OuterStamp = new(2020, 1, 29, 23, 6, 56, TimeSpan.Zero);
    private static readonly DateTimeOffset InnerStamp = new(2020, 1, 29, 23, 6, 48, TimeSpan.Zero);

    private static readonly object BuildLock = new();
    private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCacheEntries = 512;

    /// <summary>The placeholder zip bytes for one piece uuid — built once,
    /// cached (bounded; a burst of unknown ids cannot grow it past the cap).</summary>
    public static byte[] Build(string pieceUuid, string pieceType)
    {
        lock (BuildLock)
        {
            string cacheKey = $"{pieceUuid}/{pieceType}";
            if (Cache.TryGetValue(cacheKey, out byte[]? cached))
            {
                return cached;
            }

            byte[] built = BuildZip(pieceUuid, pieceType);
            if (Cache.Count >= MaxCacheEntries)
            {
                Cache.Clear();
            }

            Cache[cacheKey] = built;
            return built;
        }
    }

    private static byte[] BuildZip(string pieceUuid, string pieceType)
    {
        // normalize ONCE: meta and the geometry rig must agree on the type
        // (an empty pieceType used to write meta "persona_head" but build the
        // body rig — an internal disagreement the client can choke on)
        pieceType = string.IsNullOrWhiteSpace(pieceType) ? "persona_head" : pieceType;

        // per-file keys derived from the piece uuid — deterministic, unique per piece
        var rng = new Random(BitConverter.ToInt32(SHA256.HashData(Encoding.ASCII.GetBytes(pieceUuid)), 0));
        string metaKey = GenerateKey(rng);
        string textureKey = GenerateKey(rng);
        string geometryKey = GenerateKey(rng);

        string moduleUuid = NewGuidFromHash(SHA256.HashData(Encoding.ASCII.GetBytes($"module/{pieceUuid}")));

        string manifestJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["format_version"] = 1,
            ["header"] = new Dictionary<string, object?>
            {
                ["name"] = "Apace Placeholder",
                ["description"] = "Placeholder for a piece binary that is no longer available",
                ["uuid"] = pieceUuid,
                ["version"] = BuildVersion,
            },
            ["modules"] = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["type"] = "persona_piece",
                    ["uuid"] = moduleUuid,
                    ["version"] = BuildVersion,
                },
            },
        }, JsonOptions);

        string metaJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["piece_id"] = pieceUuid,
            ["piece_name"] = StubName,
            ["piece_type"] = pieceType,
            ["geometry_sources"] = new List<Dictionary<string, object?>>
            {
                GeometrySource("tall"), GeometrySource("medium"), GeometrySource("small"), GeometrySource("smaller"),
            },
        }, JsonOptions);

        // legacy 1.8.0 geometry — the proven cape chain, the only rig the
        // frozen client provably imports AND renders (see BuildGeometry)
        string geometryJson = JsonSerializer.Serialize(BuildGeometry(), JsonOptions);

        // trailing newline exactly like the proven builder's lang files
        string lang = $"persona.{StubName}.title=Piece\n";
        string languages = "[\"en_US\"]\n";

        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        byte[] metaBytes = PersonaContentCrypto.Encrypt(Encoding.ASCII.GetBytes(metaKey), Encoding.UTF8.GetBytes(metaJson));
        byte[] textureBytes = PersonaContentCrypto.Encrypt(Encoding.ASCII.GetBytes(textureKey), Convert.FromBase64String(PlaceholderPngBase64));
        byte[] geometryBytes = PersonaContentCrypto.Encrypt(Encoding.ASCII.GetBytes(geometryKey), Encoding.UTF8.GetBytes(geometryJson));

        // signatures.json first (hash of the FINAL manifest bytes), raw like real pieces
        string signaturesJson = JsonSerializer.Serialize(new List<Dictionary<string, string>>
        {
            new()
            {
                ["path"] = "manifest.json",
                ["hash"] = Convert.ToBase64String(SHA256.HashData(manifestBytes)),
            },
        }, JsonOptions);

        byte[] contentsBytes = PersonaContentCrypto.BuildContentsJson(pieceUuid,
        [
            ("manifest.json", null),
            ("texts/en_US.lang", null),
            ("texts/languages.json", null),
            ($"{StubName}.meta.json", metaKey),
            ($"{StubName}.png", textureKey),
            ($"{StubName}.geometry.json", geometryKey),
            // signatures.json is deliberately NOT listed: the proven-importable
            // zip's contents body lists exactly the six files above and ships
            // signatures.json raw and unlisted — an extra listed keyless entry
            // is the kind of unrecognized key that makes the client's import
            // await never complete (the hang).
        ]);

        using var outer = new MemoryStream();
        using (var outerArchive = new ZipArchive(outer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry outerEntry = outerArchive.CreateEntry("ppack0.zip");
            outerEntry.LastWriteTime = OuterStamp;
            outerEntry.ExternalAttributes = 0x1800000; // unix mode 0600, like the proven builder's entries
            using (var inner = outerEntry.Open())
            using (var innerArchive = new ZipArchive(inner, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteRaw(innerArchive, "manifest.json", manifestBytes);
                WriteRaw(innerArchive, "texts/en_US.lang", Encoding.UTF8.GetBytes(lang));
                WriteRaw(innerArchive, "texts/languages.json", Encoding.UTF8.GetBytes(languages));
                WriteRaw(innerArchive, $"{StubName}.meta.json", metaBytes);
                // geometry before texture, the proven builder's order
                WriteRaw(innerArchive, $"{StubName}.geometry.json", geometryBytes);
                WriteRaw(innerArchive, $"{StubName}.png", textureBytes);
                WriteRaw(innerArchive, "signatures.json", Encoding.ASCII.GetBytes(signaturesJson));
                WriteRaw(innerArchive, "contents.json", contentsBytes);
            }
        }

        return outer.ToArray();

        static Dictionary<string, object?> GeometrySource(string bodySize) => new()
        {
            ["geometry"] = $"geometry.{StubName}.{bodySize}",
            ["body_size"] = bodySize,
            ["texture"] = $"{StubName}.png",
        };
    }

    // ONE rig for every placeholder type, no per-type branching: the proven
    // cape chain, byte-for-byte the Apace cape's geometry (root→waist→body→
    // cape, the cape cube on the "cape" bone). History: the first placeholder
    // had EMPTY bones and imported fine (it just rendered nothing); the
    // per-type rigs (head/legs/body) were guesses and the client SILENTLY
    // HANGS applying them — no retry, no error, SetObjects never reached. The
    // cape chain is the only rig this client PROVABLY imports AND renders, so
    // every placeholder uses it: the piece applies (unblocking the persona
    // save) and shows a visible gray block on the player's back.
    private static Dictionary<string, object?> BuildGeometry() => new()
    {
        ["format_version"] = "1.8.0",
        [$"geometry.{StubName}.tall"] = GeometryFor(),
        [$"geometry.{StubName}.medium"] = GeometryFor(),
        [$"geometry.{StubName}.small"] = GeometryFor(),
        [$"geometry.{StubName}.smaller"] = GeometryFor(),
    };

    private static Dictionary<string, object?> GeometryFor()
    {
        List<Dictionary<string, object?>> bones =
        [
            Bone("root", null, 0, 0, 0, cubes: null),
            Bone("waist", "root", 0, 12, 0, cubes: null),
            Bone("body", "waist", 0, 24, 0, cubes: null),
            Bone("cape", "body", 0, 24, -3, [Cube(-5, 8, -3, 10, 16, 1)]),
        ];

        return new Dictionary<string, object?>
        {
            ["visible_bounds_width"] = 2,
            ["visible_bounds_height"] = 2,
            ["visible_bounds_offset"] = new List<object> { 0f, 0.5f, 0f },
            ["texturewidth"] = 64,
            ["textureheight"] = 64,
            ["bones"] = bones,
        };

        static Dictionary<string, object?> Bone(string name, string? parent, float px, float py, float pz, List<Dictionary<string, object?>>? cubes)
        {
            var bone = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["pivot"] = new List<object> { px, py, pz },
            };
            if (parent is not null)
            {
                bone["parent"] = parent;
            }

            if (cubes is not null)
            {
                bone["cubes"] = cubes;
            }

            return bone;
        }

        static Dictionary<string, object?> Cube(float x, float y, float z, float w, float h, float d) => new()
        {
            ["origin"] = new List<object> { x, y, z },
            ["size"] = new List<object> { w, h, d },
            ["uv"] = new List<object> { 0, 0 },
        };
    }

    private static void WriteRaw(ZipArchive archive, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        entry.LastWriteTime = InnerStamp;
        entry.ExternalAttributes = 0x1800000; // unix mode 0600, like the proven builder's entries
        using Stream stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    // 32-char alphanumeric file key, exactly like the proven builder.
    private static string GenerateKey(Random rng)
    {
        const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";
        Span<char> chars = stackalloc char[32];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[rng.Next(Alphabet.Length)];
        }

        return new string(chars);
    }

    // A valid uuid string derived from a hash — deterministic per piece, unique
    // per module, and formatted like the real pieces' module uuids.
    private static string NewGuidFromHash(byte[] hash)
    {
        Span<byte> guidBytes = hash.AsSpan(0, 16);
        // set the RFC-4122 version/variant bits so it parses as a proper uuid
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x40);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes).ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
