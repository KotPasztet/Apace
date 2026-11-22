namespace Apace.LauncherUI.Services;

// Idempotent post-processing pipeline for the Mojang assets in the persistent
// data dir. Runs AFTER the asset gate considers the assets complete — both
// right after a successful download and on every start that finds the assets
// already complete (so steps must be IDEMPOTENT: re-running them on a fully
// processed dir must be a no-op).
//
// Intended for later pipeline stages (e.g. building a Bedrock skin pack from
// skins/<Name>.png into playfab/skinpacks/<offerId>.zip, generating geometry
// jsons, etc.). Register steps at panel startup, BEFORE the first start:
//
//     MojangAssetPostProcessor.Register(async mojangAssetsDir =>
//     {
//         // build/patch derived assets under mojangAssetsDir ...
//     });
//
// Steps receive the resolved absolute Mojang assets dir. A failing step is
// logged by the gate and must not prevent the server from starting.
public static class MojangAssetPostProcessor
{
    /// <summary>Signature of a post-processing step: the resolved Mojang assets dir.</summary>
    public delegate Task PostProcessStep(string mojangAssetsDir);

    private static readonly List<PostProcessStep> Steps = [];

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
}
