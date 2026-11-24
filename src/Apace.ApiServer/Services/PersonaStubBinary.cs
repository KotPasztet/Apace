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
// proven-working shape (scripts/build_apace_piece.py) whose geometry carries a
// single empty bone — the client imports it, applies the piece, renders
// nothing and the equip completes instantly.
//
// Deterministic by construction: one piece uuid always yields the same bytes
// (fixed timestamps, uuid-derived file keys), so a client's cached copy of the
// placeholder stays consistent across rebuilds.
public static class PersonaStubBinary
{
    private const string StubName = "apace_stub";

    // 1×1 fully transparent PNG — the placeholder texture.
    private const string TransparentPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR42mNgAAIAAAUAAen63NgAAAAASUVORK5CYII=";

    // The manifest header/module version the client re-imports on ([3,0,0] —
    // same reasoning as the cape builder: a bump forces the client to use the
    // fresh pack instead of a cached copy of a previous one).
    private static readonly int[] BuildVersion = [3, 0, 0];

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
            ["piece_type"] = string.IsNullOrWhiteSpace(pieceType) ? "persona_head" : pieceType,
            ["geometry_sources"] = new List<Dictionary<string, object?>>
            {
                GeometrySource("tall"), GeometrySource("medium"), GeometrySource("small"), GeometrySource("smaller"),
            },
        }, JsonOptions);

        // legacy 1.8.0 geometry — the shape the frozen client provably accepts
        // (the Apace capes ship it); one root bone, no cubes — renders nothing
        string geometryJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["format_version"] = "1.8.0",
            [$"geometry.{StubName}.tall"] = EmptyGeometry(),
            [$"geometry.{StubName}.medium"] = EmptyGeometry(),
            [$"geometry.{StubName}.small"] = EmptyGeometry(),
            [$"geometry.{StubName}.smaller"] = EmptyGeometry(),
        }, JsonOptions);

        string lang = $"persona.{StubName}.title=Piece";
        string languages = "[\"en_US\"]";

        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        byte[] metaBytes = PersonaContentCrypto.Encrypt(Encoding.ASCII.GetBytes(metaKey), Encoding.UTF8.GetBytes(metaJson));
        byte[] textureBytes = PersonaContentCrypto.Encrypt(Encoding.ASCII.GetBytes(textureKey), Convert.FromBase64String(TransparentPngBase64));
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
            ("signatures.json", null),
        ]);

        using var outer = new MemoryStream();
        using (var outerArchive = new ZipArchive(outer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry outerEntry = outerArchive.CreateEntry("ppack0.zip");
            outerEntry.LastWriteTime = OuterStamp;
            using (var inner = outerEntry.Open())
            using (var innerArchive = new ZipArchive(inner, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteRaw(innerArchive, "manifest.json", manifestBytes);
                WriteRaw(innerArchive, "texts/en_US.lang", Encoding.UTF8.GetBytes(lang));
                WriteRaw(innerArchive, "texts/languages.json", Encoding.UTF8.GetBytes(languages));
                WriteRaw(innerArchive, $"{StubName}.meta.json", metaBytes);
                WriteRaw(innerArchive, $"{StubName}.png", textureBytes);
                WriteRaw(innerArchive, $"{StubName}.geometry.json", geometryBytes);
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

        static Dictionary<string, object?> EmptyGeometry() => new()
        {
            ["visible_bounds_width"] = 1,
            ["visible_bounds_height"] = 1,
            ["visible_bounds_offset"] = new List<object> { 0f, 0.5f, 0f },
            ["texturewidth"] = 1,
            ["textureheight"] = 1,
            ["bones"] = new List<Dictionary<string, object?>>
            {
                new() { ["name"] = "root", ["pivot"] = new List<object> { 0f, 0f, 0f } },
            },
        };
    }

    private static void WriteRaw(ZipArchive archive, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name);
        entry.LastWriteTime = InnerStamp;
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
