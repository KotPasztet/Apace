using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Apace.LauncherUI.Controllers;

// Persona (skin) item thumbnails are PNGs in the ApiServer's wwwroot, served to
// game clients at /playfab/images/<id>.png. The panel is a separate process, so
// it re-serves the same files through this controller — plus a lazy resolve of
// its own: when no local copy exists, LivePersonaCatalogSync downloads the
// thumbnail from the URL recorded at sync time (else from the Bedrock-Persona
// mirror) into the volume cache, so the skins tab works without waiting for a
// game client to warm the ApiServer's cache first.
[ApiController]
[Authorize(Policy = Permissions.ManagePlayers)]
[Route("api/persona-images")]
internal sealed class PersonaImagesController : ControllerBase
{
    // Published layout: the ApiServer is published into the components dir next
    // to the panel, thumbnails end up in its wwwroot. When running from the repo
    // (bin/Debug/net10.0), fall back to the ApiServer project directory instead.
    // The data volume dirs cover the rest: the asset gate downloads the dressing
    // room thumbnails into mojang-assets/images and the live persona catalog
    // caches marketplace thumbnails into persona-catalog/thumbnails. Resolved per
    // request (expression-bodied) — Program.DataDir is set after static init.
    private static string[] ImagesDirs =>
    [
        Path.Combine(Program.ProgramsDir, "wwwroot", "playfab", "images"),
        Path.Combine(Services.MojangAssetGate.MojangAssetsDir, "images"),
        Path.Combine(Services.MojangAssetGate.MojangAssetsDir, "persona-catalog", "thumbnails"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Apace.ApiServer", "wwwroot", "playfab", "images")),
    ];

    [HttpGet("{id}.png")]
    [HttpHead("{id}.png")]
    public static async Task<Results<PhysicalFileHttpResult, NotFound>> GetThumbnail(string id, CancellationToken cancellationToken)
    {
        // Thumbnail file names are GUIDs; refuse anything else so the path can't be abused for traversal.
        if (!Guid.TryParse(id, out _))
        {
            return TypedResults.NotFound();
        }

        foreach (string dir in ImagesDirs)
        {
            string path = Path.Combine(dir, id + ".png");

            if (System.IO.File.Exists(path))
            {
                return TypedResults.PhysicalFile(path);
            }
        }

        // Miss: resolve lazily — the sync-time URL from the index, then the mirror.
        string? resolved = await Services.LivePersonaCatalogSync.TryEnsureThumbnailAsync(id, Log.Logger, cancellationToken);
        if (resolved is not null && System.IO.File.Exists(resolved))
        {
            return TypedResults.PhysicalFile(resolved);
        }

        return TypedResults.NotFound();
    }
}
