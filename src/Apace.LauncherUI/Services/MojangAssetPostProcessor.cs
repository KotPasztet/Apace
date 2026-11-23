using Serilog;
using ILogger = Serilog.ILogger;

namespace Apace.LauncherUI.Services;

// Idempotent post-processing pipeline for the Mojang assets in the persistent
// data dir. Runs on every panel start (both the full Start flow and page-level
// component starts — so steps must be IDEMPOTENT: re-running them on a fully
// processed dir must be a no-op).
//
// Intended for pipeline stages that derive servable assets from the volume
// (e.g. building a Bedrock skin pack from skins/<Name>.png into
// playfab/skinpacks/<offerId>.zip, generating geometry jsons, etc.). Steps are
// registered HERE so every panel start runs the full pipeline — currently:
//
//     MojangSkinTextureFetcher.FetchAsync (fetches the default skin textures
//                                          recorded in mojang-assets.manifest.json
//                                          into skins/ — the pack build's inputs)
//     MojangSkinPackBuilder.BuildAsync  ("Minecraft Default Skins" pack from
//                                       the default skin textures)
//
// Additional steps can be appended to the Steps initializer below (or extra
// Register calls added at panel startup, BEFORE the first start):
//
//     MojangAssetPostProcessor.Register(async mojangAssetsDir =>
//     {
//         // build/patch derived assets under mojangAssetsDir ...
//     });
//
// Steps receive the resolved absolute Mojang assets dir. Startup paths call the
// pipeline through RunAllSafe: a failing step is logged and never blocks the
// server start.
public static class MojangAssetPostProcessor
{
    /// <summary>Signature of a post-processing step: the resolved Mojang assets dir.</summary>
    public delegate Task PostProcessStep(string mojangAssetsDir);

    // Order matters: the fetcher produces the inputs (skins/*.png) the pack
    // builder consumes in the same pipeline pass.
    private static readonly List<PostProcessStep> Steps = [MojangSkinTextureFetcher.FetchAsync, MojangSkinPackBuilder.BuildAsync];

    /// <summary>Registers a post-processing step (call once at startup; duplicates are ignored).</summary>
    public static void Register(PostProcessStep step)
    {
        if (!Steps.Contains(step))
        {
            Steps.Add(step);
        }
    }

    /// <summary>Runs all registered steps against the assets dir. Safe to call repeatedly.</summary>
    public static async Task RunAll(string mojangAssetsDir)
    {
        foreach (var step in Steps.ToArray())
        {
            await step(mojangAssetsDir);
        }
    }

    /// <summary>
    /// Runs the pipeline best-effort from the startup paths: never throws — a
    /// failing step is logged and the start proceeds (the derived assets are
    /// rebuilt on the next start).
    /// </summary>
    public static async Task RunAllSafe(string mojangAssetsDir, ILogger logger)
    {
        try
        {
            await RunAll(mojangAssetsDir);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Mojang asset post-processing failed (asset files are in place; every step must be idempotent)");
        }
    }
}
