using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Apace.ApiServer.Services;

// The Skin & Persona pack crypto: every marketplace piece zip carries an inner
// ppack0.zip whose contents.json lists the art files and their per-file keys.
// The contents body and the per-file payloads are AES-256-CFB8 with the
// client's built-in "Skin & Persona Key" (IV = key[:16]) — verified against a
// real marketplace piece sample, see scripts/build_apace_piece.py (the same
// key InventoryController.ApaceContentKey carries for the entitlement
// receipts). Shared by the geometry probe (decrypts piece zips so the compat
// facts can be read at all — without it every real marketplace piece records
// all-unknown facts) and the placeholder builder (builds valid piece zips).
public static class PersonaContentCrypto
{
    // The client's built-in key — "used to decrypt/encrypt every Skin Pack and
    // Persona on the Minecraft Marketplace... the same key is used every
    // single time" (found inside the client binary).
    public const string SkinPersonaKey = "s5s5ejuDru4uchuF2drUFuthaspAbepE";

    private static readonly byte[] KeyBytes = Encoding.ASCII.GetBytes(SkinPersonaKey);

    private const int ContentsHeaderBytes = 256;

    // AES-256-CFB8 (8-bit feedback, stream cipher), zero-padded to a 16-byte
    // boundary exactly like the proven-working builder — the client stops
    // parsing at the JSON end / file end, so the unaligned tails are fine.
    // (The .NET 10 cipher-mode enum members are uppercase: CipherMode.CFB.)
    public static byte[] Encrypt(byte[] key, byte[] data)
    {
        int pad = (16 - data.Length % 16) % 16;
        if (pad > 0)
        {
            byte[] padded = new byte[data.Length + pad];
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            data = padded;
        }

        using Aes aes = Aes.Create();
        aes.Key = NormalizeKey(key);
        aes.IV = aes.Key.AsSpan(0, 16).ToArray(); // IV = key[:16]
        aes.Mode = CipherMode.CFB;
        aes.FeedbackSize = 8;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }

    public static byte[] Decrypt(byte[] key, byte[] data)
    {
        using Aes aes = Aes.Create();
        aes.Key = NormalizeKey(key);
        aes.IV = aes.Key.AsSpan(0, 16).ToArray(); // IV = key[:16]
        aes.Mode = CipherMode.CFB;
        aes.FeedbackSize = 8;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }

    // 32 ASCII chars = the AES-256 key; the IV is the first 16 bytes of it.
    private static byte[] NormalizeKey(byte[] key)
    {
        if (key.Length == 32)
        {
            return key;
        }

        byte[] normalized = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            normalized[i] = key[i % key.Length];
        }

        return normalized;
    }

    /// <summary>Decrypts one per-file-encrypted entry (key = the contents.json
    /// "key" value, ASCII) and strips the zero padding. Null when the key does
    /// not decrypt (foreign piece, corrupt bytes).</summary>
    public static byte[]? TryDecryptEntry(byte[] bytes, string fileKey)
    {
        try
        {
            byte[] plain = Decrypt(Encoding.ASCII.GetBytes(fileKey), bytes);
            int end = plain.Length;
            while (end > 0 && plain[end - 1] == 0)
            {
                end--;
            }

            return plain[..end];
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Parses a piece zip's contents.json (256-byte ContentIdentity
    /// header + AES-256-CFB8 body with the built-in key) into its
    /// path → per-file-key map — the map the probe needs to decrypt the art
    /// files inside ppack0.zip. Null when the body does not decrypt or parse.</summary>
    public static Dictionary<string, string>? TryReadContentsKeys(byte[] contentsBytes)
    {
        try
        {
            if (contentsBytes.Length < ContentsHeaderBytes + 16
                || contentsBytes[0x04] != 0xFC || contentsBytes[0x05] != 0xB9
                || contentsBytes[0x06] != 0xCF || contentsBytes[0x07] != 0x9B)
            {
                return null;
            }

            byte[] body = Decrypt(KeyBytes, contentsBytes[ContentsHeaderBytes..]);
            int end = body.Length;
            while (end > 0 && body[end - 1] == 0)
            {
                end--;
            }

            var file = JsonSerializer.Deserialize<ContentsFileDto>(body[..end], JsonOptions);
            if (file?.Content is null)
            {
                return null;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ContentsEntry? entry in file.Content)
            {
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.Path) && !string.IsNullOrWhiteSpace(entry.Key))
                {
                    map[entry.Path] = entry.Key;
                }
            }

            return map;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Builds a piece zip's contents.json: the 256-byte ContentIdentity
    /// header (u32@0x00 = 0, u32@0x04 = 0x9BCFB9FC LE, uuid ASCII at 0x11,
    /// zero padding) plus the AES-256-CFB8-encrypted body listing every entry
    /// ({"key","path"} for per-file-encrypted files, {"path"} for raw ones).</summary>
    public static byte[] BuildContentsJson(string headerUuid, List<(string Path, string? FileKey)> entries)
    {
        var header = new byte[ContentsHeaderBytes];
        header[0x04] = 0xFC;
        header[0x05] = 0xB9;
        header[0x06] = 0xCF;
        header[0x07] = 0x9B;
        byte[] uuidBytes = Encoding.ASCII.GetBytes(headerUuid);
        header[0x10] = (byte)uuidBytes.Length;
        Buffer.BlockCopy(uuidBytes, 0, header, 0x11, uuidBytes.Length);

        // object-typed values serialize by runtime type, so raw entries carry
        // no "key" member at all — exactly the shape the real pieces ship
        var content = new List<object>(entries.Count);
        foreach ((string path, string? fileKey) in entries)
        {
            content.Add(fileKey is null ? new RawEntry(path) : new KeyedEntry(fileKey, path));
        }

        string body = JsonSerializer.Serialize(new ContentsFile(1, content), JsonOptions);
        return [.. header, .. Encrypt(KeyBytes, Encoding.UTF8.GetBytes(body))];
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Serialization shape (runtime-typed): {"key","path"} or {"path"}.
    private sealed record ContentsFile(int Version, List<object> Content);

    // Deserialization shape (keys optional).
    private sealed record ContentsFileDto(int Version, List<ContentsEntry?> Content);

    private sealed record KeyedEntry(string Key, string Path);

    private sealed record RawEntry(string Path);

    // Deserialization shape (keys optional).
    private sealed record ContentsEntry(string? Key, string? Path);
}
