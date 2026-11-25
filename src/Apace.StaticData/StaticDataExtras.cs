namespace Apace.StaticData;

/// <summary>
/// Per-file "extras win" lookup for the in-repo Apace data overlay: <c>staticdata-extras/</c>
/// mirrors the staticdata layout and its files win over the Solace.StaticData submodule.
/// publish.ps1 copies the overlay into the deployed staticdata tree, so in deployments the
/// base files already carry the overlay; in dev checkouts (where the staticdata submodule is
/// the plain upstream tree) the loaders probe the overlay as a sibling of the staticdata
/// directory instead — the same overlay the MojangAssets playfab probing uses.
/// </summary>
public static class StaticDataExtras
{
    /// <summary>
    /// Returns the path of a file inside a staticdata subdirectory, preferring the copy in the
    /// overlay directory when one exists there. <paramref name="extrasDir"/> may be null
    /// (no overlay found); the overlay may also simply not carry that file.
    /// </summary>
    public static string ResolveFile(string dir, string? extrasDir, string relativePath)
    {
        if (extrasDir is not null)
        {
            string extrasFile = Path.Combine(extrasDir, relativePath);
            if (File.Exists(extrasFile))
            {
                return extrasFile;
            }
        }

        return Path.Combine(dir, relativePath);
    }
}
