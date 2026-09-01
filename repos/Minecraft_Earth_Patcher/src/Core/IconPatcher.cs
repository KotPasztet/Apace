using System.Text.RegularExpressions;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace MCEPatcher.Core;

/// <summary>
/// Replaces the launcher icon of the decoded app with the provided image,
/// scaling it to the exact pixel dimensions of every original icon file.
/// </summary>
public static class IconPatcher
{
    /// <summary>
    /// Android: replaces the launcher icon referenced by the manifest
    /// (android:icon / android:roundIcon of the &lt;application&gt; element, e.g. MCE's
    /// "@drawable/icon_earth") in every density folder, scaled to each file's original
    /// size. Adaptive-icon xml wrappers are removed so the bitmaps also show on Android 8+.
    /// Falls back to the classic res/mipmap*/ic_launcher* layout when the manifest
    /// does not reference icon files (e.g. plain "@mipmap/ic_launcher" adaptive apps).
    /// </summary>
    public static void PatchAndroidIcon(string decodedDir, string iconPath)
    {
        string resDir = Path.Combine(decodedDir, "res");

        if (!Directory.Exists(resDir))
        {
            Log.Warning("res directory not found, skipping icon patch");
            return;
        }

        int replaced = 0;

        foreach ((string type, string name) in GetManifestIconResources(decodedDir))
        {
            // e.g. "drawable" matches drawable, drawable-mdpi-v4, drawable-xhdpi-v4, ...
            foreach (string dir in Directory.EnumerateDirectories(resDir, type + "*"))
            {
                foreach (string iconFile in Directory.EnumerateFiles(dir, name + ".*"))
                {
                    switch (Path.GetExtension(iconFile).ToLowerInvariant())
                    {
                        case ".png":
                        case ".webp":
                            ReplaceWithScaledIcon(iconFile, iconPath);
                            replaced++;
                            break;
                        case ".xml":
                            // adaptive-icon wrapper would override the bitmaps on Android 8+
                            File.Delete(iconFile);
                            Log.Debug($"Removed adaptive icon '{iconFile}'");
                            break;
                    }
                }
            }
        }

        if (replaced == 0)
        {
            // classic layout (not used by Minecraft Earth, kept for other apps)
            foreach (string mipmap in Directory.EnumerateDirectories(resDir, "mipmap*"))
            {
                string dirName = Path.GetFileName(mipmap);

                if (dirName.StartsWith("mipmap-anydpi", StringComparison.OrdinalIgnoreCase))
                {
                    // adaptive icon xml would override the bitmaps on Android 8+
                    foreach (string xml in Directory.EnumerateFiles(mipmap, "ic_launcher*.xml"))
                    {
                        File.Delete(xml);
                        Log.Debug($"Removed adaptive icon '{xml}'");
                    }

                    continue;
                }

                foreach (string iconFile in Directory.EnumerateFiles(mipmap, "ic_launcher*.png"))
                {
                    ReplaceWithScaledIcon(iconFile, iconPath);
                    replaced++;
                }

                foreach (string iconFile in Directory.EnumerateFiles(mipmap, "ic_launcher*.webp"))
                {
                    ReplaceWithScaledIcon(iconFile, iconPath);
                    replaced++;
                }
            }
        }

        if (replaced == 0)
        {
            Log.Warning("No launcher icons found in res/, the icon was not changed");
        }
    }

    /// <summary>
    /// Extracts the (type, name) resource pairs the manifest's &lt;application&gt; element
    /// points at via android:icon / android:roundIcon, e.g. ("drawable", "icon_earth").
    /// The decoded AndroidManifest.xml is plain text after apktool d.
    /// </summary>
    private static IEnumerable<(string Type, string Name)> GetManifestIconResources(string decodedDir)
    {
        string manifestPath = Path.Combine(decodedDir, "AndroidManifest.xml");

        if (!File.Exists(manifestPath))
        {
            Log.Warning("AndroidManifest.xml not found, cannot determine the launcher icon resource");
            return [];
        }

        string manifest = File.ReadAllText(manifestPath);

        // the <application ...> element with all its attributes (the decoded manifest is
        // pretty-printed, but attributes always sit between the tag and its '>')
        int appStart = manifest.IndexOf("<application", StringComparison.OrdinalIgnoreCase);

        if (appStart < 0)
        {
            return [];
        }

        int appEnd = manifest.IndexOf('>', appStart);
        string application = appEnd < 0 ? "" : manifest[appStart..appEnd];

        var pairs = new List<(string, string)>();
        var seen = new HashSet<(string, string)>();

        foreach (Match match in Regex.Matches(application,
                     "android:(?:round)?icon=\"@(\\w+)/(\\w+)\""))
        {
            var pair = (match.Groups[1].Value, match.Groups[2].Value);

            // only drawable/mipmap images can be replaced; skip e.g. @style or color refs
            if (pair.Item1 is not ("drawable" or "mipmap"))
            {
                continue;
            }

            if (seen.Add(pair))
            {
                pairs.Add(pair);
            }
        }

        return pairs;
    }

    /// <summary>iOS: replaces every AppIcon*.png in the .app bundle with the icon.</summary>
    public static void PatchIosIcon(string appDir, string iconPath)
    {
        int replaced = 0;

        foreach (string iconFile in Directory.EnumerateFiles(appDir, "AppIcon*.png"))
        {
            ReplaceWithScaledIcon(iconFile, iconPath);
            replaced++;
        }

        if (replaced == 0)
        {
            Log.Warning("No AppIcon*.png files found in the app bundle - the icon cannot be changed (compiled asset catalogs are not supported)");
        }
    }

    private static void ReplaceWithScaledIcon(string targetFile, string iconPath)
    {
        using Image target = Image.Load(targetFile);
        using Image icon = Image.Load(iconPath);

        icon.Mutate(o => o.Resize(target.Width, target.Height));

        // keep the original file format
        if (Path.GetExtension(targetFile).Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            icon.SaveAsWebp(targetFile);
        }
        else
        {
            icon.SaveAsPng(targetFile);
        }

        Log.Debug($"Replaced '{targetFile}' ({target.Width}x{target.Height})");
    }
}