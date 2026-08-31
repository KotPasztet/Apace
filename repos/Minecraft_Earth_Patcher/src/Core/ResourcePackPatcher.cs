using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Serilog;

namespace MCEPatcher.Core;

internal static class ResourcePackPatcher
{
    private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
    };

    public static async Task<bool> Patch(ApkProcessor.Options options)
    {
        if (!File.Exists(options.ResourcePack))
        {
            Log.Fatal($"Resource pack file '{options.ResourcePack}' does not exist");
            return false;
        }

        var resourcePackFolder = Path.Combine("tmp", "resourcepack");

        try
        {
            Log.Information("Extracting resource pack");

            using (var resourcePackFS = File.OpenRead(options.ResourcePack))
            using (var resourcePackZip = new ZipArchive(resourcePackFS, ZipArchiveMode.Read))
            {
                var genoaEntry = resourcePackZip.GetEntry("genoa.mcpack");

                if (genoaEntry is null)
                {
                    Log.Fatal("Invalid resourcepack file, 'genoa.mcpack' missing");
                    return false;
                }

                using (var genoaStream = await genoaEntry.OpenAsync())
                using (var genoaZip = new ZipArchive(genoaStream, ZipArchiveMode.Read))
                {
                    await genoaZip.ExtractToDirectoryAsync(resourcePackFolder, overwriteFiles: true);
                }
            }

            Log.Debug("Done");

            Log.Information("Copying resource pack files");

            File.Delete(Path.Combine(resourcePackFolder, "manifest.json")); // So it doesn't overwrite the existing one in vanilla_base

            var resourcePackRoot = Path.Combine(options.DecodedDir, "assets", "resource_packs", "vanilla_base");
            Log.Information(resourcePackFolder + " " + resourcePackRoot);
            U.CopyDir(resourcePackFolder, resourcePackRoot, overwrite: true);

            Log.Debug("Done");

            Log.Information("Patching resource pack json files");

            Log.Information("Generating _ui_defs.json");

            List<string> contents = [];
            var uiDirectory = Path.Combine(resourcePackRoot, "ui");
            foreach (var file in Directory.EnumerateFiles(uiDirectory))
            {
                if (Path.GetExtension(file.AsSpan()).Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(file.AsSpan()) is not "_ui_defs.json")
                {
                    var pathWithoutExt = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file));

                    var relativePath = pathWithoutExt.Replace(resourcePackRoot, "").Replace('\\', '/');

                    if (relativePath.StartsWith('/'))
                    {
                        relativePath = relativePath[1..];
                    }

                    if (relativePath.StartsWith("ui/animation/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    contents.Add(relativePath);
                }
            }

            File.WriteAllText(
                Path.Combine(resourcePackRoot, "ui", "_ui_defs.json"),
                JsonSerializer.Serialize(new { ui_defs = contents.Order(), }),
                Encoding.UTF8);

            Log.Debug("Done");

            Log.Information("Generating textures_list.json");

            var textureTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".tga", ".tiff", ".png", ".jpg"
                };
            contents.Clear();

            foreach (var file in Directory.EnumerateFiles(Path.Combine(resourcePackRoot, "textures"), "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);

                if (textureTypes.Contains(extension))
                {
                    var pathWithoutExt = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file));

                    var relativePath = pathWithoutExt.Replace(resourcePackRoot, "").Replace('\\', '/');

                    if (relativePath.StartsWith('/'))
                    {
                        relativePath = relativePath[1..];
                    }

                    contents.Add(relativePath);
                }
            }

            File.WriteAllText(
                Path.Combine(resourcePackRoot, "textures", "textures_list.json"),
                JsonSerializer.Serialize(contents, jsonOptions),
                Encoding.UTF8);

            Log.Debug("Done");

            Log.Information("Generating contents.json");

            var contents2 = new List<Dictionary<string, string>>();

            foreach (string file in Directory.EnumerateFiles(resourcePackRoot, "*", SearchOption.AllDirectories))
            {
                var relativePath = file.Replace(resourcePackRoot, "").Replace("\\", "/");

                if (relativePath.StartsWith('/'))
                {
                    relativePath = relativePath[1..];
                }

                contents2.Add(new Dictionary<string, string>
                    {
                        { "path", relativePath }
                    });
            }

            File.WriteAllText(
                Path.Combine(resourcePackRoot, "contents.json"),
                JsonSerializer.Serialize(new { content = contents2.OrderBy(item => item.First().Key), }, jsonOptions),
                Encoding.UTF8);

            Log.Debug("Done");

            Log.Debug("Done");

            return true;
        }
        catch (Exception exception)
        {
            Log.Fatal($"Failed to patch resource pack: {exception}");
            return false;
        }
        finally
        {
            // Directory.Delete(resourcePackFolder, true);
        }
    }
}