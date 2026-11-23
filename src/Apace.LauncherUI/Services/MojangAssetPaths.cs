namespace Apace.LauncherUI.Services;

// Resolves the persistent Mojang assets directory (<DataDir>/mojang-assets —
// /app/data/mojang-assets in docker). It holds the persona piece binaries,
// dressing-room thumbnails, default skin textures and the live persona
// catalog cache; the CDN controllers, the catalog services and the panel
// pages resolve their serving paths against it. Nothing gates on the
// directory's contents — missing legacy pieces may 404, the live marketplace
// sync fills the volume at runtime.
public static class MojangAssetPaths
{
    private const string AssetsDirName = "mojang-assets";

    /// <summary>Persistent directory holding the assets (<c>/app/data/mojang-assets</c> in docker).</summary>
    public static string MojangAssetsDir => Path.Combine(Program.DataDir, AssetsDirName);
}
