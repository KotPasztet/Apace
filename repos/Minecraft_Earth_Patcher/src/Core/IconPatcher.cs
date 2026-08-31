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
    /// Android: replaces every res/mipmap-*/ic_launcher*.{png,webp} with the icon
    /// scaled to each file's original size, and removes the adaptive-icon xml
    /// wrappers so the bitmaps are also shown on Android 8+.
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

        foreach (string mipmap in Directory.EnumerateDirectories(resDir, "mipmap*"))
        {
            string name = Path.GetFileName(mipmap);

            if (name.StartsWith("mipmap-anydpi", StringComparison.OrdinalIgnoreCase))
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

        if (replaced == 0)
        {
            Log.Warning("No launcher icons found in res/mipmap*, the icon was not changed");
        }
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