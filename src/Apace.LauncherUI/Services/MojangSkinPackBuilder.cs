using Serilog;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ILogger = Serilog.ILogger;

namespace Apace.LauncherUI.Services;

// Builds the "Minecraft Default Skins" Bedrock skin pack zip from the default
// character textures in <mojang-assets>/skins/
// (Steve/Alex/Sunny/Kai/Makena/Zuri/Efe/Ari/Noor.png).
//
// The zip lands in <mojang-assets>/playfab/skinpacks/<offer id>.zip — the same
// persistent dir the SkinPackCdnController serves /playfab/skinpacks/ from — so
// the catalog offer staticdata-extras/playfab/items/<offer id>.json (tags
// skinpack + has_skinpack, Classic Skins row) resolves its skinbinary Contents
// entry against a pack built from the volume's textures. The repo never ships
// the pack or the textures.
//
// Pack identity: the manifest header uuid is acf11539-f3ca-4ac6-b3a9-fdd1c2cf3806
// — the uuid the MCE client hardcodes a catalog search for (inherited from the
// City Folk offer this pack replaced) and the offer's
// displayProperties.packIdentity[0].uuid. The client imports the downloaded zip
// only when that header uuid matches the promised packIdentity uuid, so the two
// constants below must stay in lockstep with the offer JSON.
//
// Structure mirrors the servable Apace Skins pack (42a5f927-….zip 1:1):
// manifest.json (format_version 1, one skin_pack module), skins.json (vanilla
// convention: wide skins → geometry.humanoid.custom, slim skins →
// geometry.humanoid.customSlim, type "free", no cape field) and
// texts/en_US.lang, with the skin textures at the zip root.
//
// Contract (see MojangAssetPostProcessor): re-run idempotently on every panel
// start; a failing step is logged and must never block the server start — this
// class catches everything and degrades to "pack not built this run".
public static class MojangSkinPackBuilder
{
    /// <summary>Catalog offer id (= zip file name stem, served by SkinPackCdnController).</summary>
    public const string OfferId = "90860f69-bcc5-46c4-9e76-b56b9db5d729";

    /// <summary>
    /// Bedrock pack header uuid — must equal the offer's packIdentity[0].uuid
    /// (and therefore the client's hardcoded skin pack search tag).
    /// </summary>
    public const string PackUuid = "acf11539-f3ca-4ac6-b3a9-fdd1c2cf3806";

    /// <summary>
    /// Bedrock pack version (both header and module; must equal the offer's
    /// packIdentity[0].version). Bumped to 1.0.1: the client caches imports per
    /// (uuid, version), so a version bump is the only way to force a re-import of
    /// a pack it once imported empty (e.g. requested before the zip existed).
    /// </summary>
    public const string PackVersion = "1.0.1";

    private const string ModuleUuid = "023c897b-9573-4f1b-b5cd-19445b70a08b";
    private const string PackName = "Minecraft Default Skins";
    private const string SerializeName = "MinecraftDefaultSkins";

    // The nine default skins: wide (classic 4px arms) vs slim (3px arms).
    private static readonly (string Name, bool Slim)[] DefaultSkins =
    [
        ("Steve", false),
        ("Alex", true),
        ("Sunny", false),
        ("Kai", false),
        ("Makena", true),
        ("Zuri", false),
        ("Efe", true),
        ("Ari", false),
        ("Noor", true),
    ];

    // Fixed zip entry timestamps so rebuilt packs stay byte-stable when the
    // inputs did not change.
    private static readonly DateTimeOffset EntryTimestamp = new(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);

    // "1, 0, 1" — the JSON array body for the header/module versions
    private static readonly string PackVersionJson = string.Join(", ", PackVersion.Split('.'));

    private static readonly string ManifestJson =
        $$"""
        {
            "format_version": 1,
            "header": {
                "name": "Minecraft Default Skins",
                "uuid": "{{PackUuid}}",
                "version": [
                    {{PackVersionJson}}
                ]
            },
            "modules": [
                {
                    "type": "skin_pack",
                    "uuid": "{{ModuleUuid}}",
                    "version": [
                        {{PackVersionJson}}
                    ]
                }
            ]
        }
        """;

    private static readonly string SkinsJson = BuildSkinsJson();

    private static readonly string EnUsLang = BuildLang();

    /// <summary>The post-processor step delegate (registered in MojangAssetPostProcessor).</summary>
    public static Task BuildAsync(string mojangAssetsDir)
    {
        try
        {
            Build(mojangAssetsDir, Log.Logger);
        }
        catch (Exception exception)
        {
            // Post-processor contract: never throw — startup logs failures but
            // must not be blocked by a derived-asset step.
            Log.Logger.Warning(exception, "Minecraft Default Skins pack build failed (non-fatal)");
        }

        return Task.CompletedTask;
    }

    private static void Build(string mojangAssetsDir, ILogger logger)
    {
        string skinsDir = Path.Combine(mojangAssetsDir, "skins");
        string targetPath = Path.Combine(mojangAssetsDir, "playfab", "skinpacks", OfferId + ".zip");

        // Collect the textures that are actually present; missing ones are
        // skipped with a warning (an install that fetched only part of the
        // skin set still gets a working pack with the rest).
        var included = new List<(string Name, bool Slim, string Path)>();
        foreach ((string name, bool slim) in DefaultSkins)
        {
            string texturePath = Path.Combine(skinsDir, name + ".png");
            var info = new FileInfo(texturePath);
            if (info.Exists && info.Length > 0)
            {
                included.Add((name, slim, texturePath));
            }
            else
            {
                logger.Warning("Minecraft Default Skins pack: {Skin} texture missing at {Path} — skipping that skin", name, texturePath);
            }
        }

        if (included.Count == 0)
        {
            logger.Warning("Minecraft Default Skins pack: no default skin textures under {SkinsDir} — pack not built", skinsDir);
            return;
        }

        // Idempotency: (re)build only when the zip is absent, empty, older than
        // the newest source texture (e.g. the gate re-downloaded one), or carries
        // an older pack version — a version bump must reach the client as a new
        // zip, or it will keep serving its cached import of the old one.
        var targetInfo = new FileInfo(targetPath);
        if (targetInfo.Exists && targetInfo.Length > 0 && PackVersionOnDisk(targetPath) == PackVersion)
        {
            DateTimeOffset newestTexture = included.Max(entry => new FileInfo(entry.Path).LastWriteTimeUtc);
            if (targetInfo.LastWriteTimeUtc >= newestTexture)
            {
                // Information, not Debug: the deploy log should always show which
                // pack version the volume carries (a stale version is the #1 reason
                // the client keeps its cached empty import and never re-downloads)
                logger.Information(
                    "Minecraft Default Skins pack up to date at {Path} (version {Version}, {Skins} skins)",
                    targetPath, PackVersion, included.Count);
                return;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        // Write to a temp file first so a crash mid-build never leaves a
        // half-written pack that the client would happily download.
        string partPath = targetPath + ".part";
        using (var stream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            AddTextEntry(zip, "manifest.json", ManifestJson);
            AddTextEntry(zip, "skins.json", SkinsJson);
            AddTextEntry(zip, "texts/en_US.lang", EnUsLang);
            foreach ((string name, _, string texturePath) in included)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name + ".png", CompressionLevel.Optimal);
                entry.LastWriteTime = EntryTimestamp;
                using var source = File.OpenRead(texturePath);
                using var entryStream = entry.Open();
                source.CopyTo(entryStream);
            }
        }

        File.Move(partPath, targetPath, overwrite: true);
        logger.Information(
            "Minecraft Default Skins pack built at {Path} ({Skins}/{Total} default skins, pack uuid {PackUuid})",
            targetPath, included.Count, DefaultSkins.Length, PackUuid);
    }

    // header.version of the pack zip currently on disk ("1.0.1") — null when the
    // zip is unreadable or malformed (anything unreadable gets rebuilt).
    private static string? PackVersionOnDisk(string targetPath)
    {
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(targetPath);
            ZipArchiveEntry? entry = zip.GetEntry("manifest.json");
            if (entry is null)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(entry.Open());
            return string.Join(".", doc.RootElement.GetProperty("header").GetProperty("version")
                .EnumerateArray().Select(part => part.GetRawText()));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string BuildSkinsJson()
    {
        var skins = new StringBuilder();
        foreach ((string name, bool slim) in DefaultSkins)
        {
            if (skins.Length > 0)
            {
                skins.AppendLine(",");
            }

            skins.Append(CultureInfo.InvariantCulture, $$"""
                    {
                      "localization_name": "{{name}}",
                      "geometry": "{{(slim ? "geometry.humanoid.customSlim" : "geometry.humanoid.custom")}}",
                      "texture": "{{name}}.png",
                      "type": "free"
                    }
                """);
        }

        return
            $$"""
            {
              "skins": [
            {{skins}}
              ],
              "serialize_name": "{{SerializeName}}",
              "localization_name": "{{SerializeName}}"
            }
            """;
    }

    private static string BuildLang()
    {
        var lines = new StringBuilder();
        lines.AppendLine(CultureInfo.InvariantCulture, $"skinpack.{SerializeName}={PackName}");
        foreach ((string name, _) in DefaultSkins)
        {
            lines.AppendLine(CultureInfo.InvariantCulture, $"skin.{SerializeName}.{name}={name}");
        }

        return lines.ToString();
    }

    private static void AddTextEntry(ZipArchive zip, string entryName, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTimestamp;
        using var entryStream = entry.Open();
        entryStream.Write(Encoding.UTF8.GetBytes(content));
    }
}
