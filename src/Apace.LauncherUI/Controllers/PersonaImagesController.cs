using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Apace.LauncherUI.Controllers;

// Persona (skin) item thumbnails are PNGs in the ApiServer's wwwroot, served to
// game clients at /playfab/images/<id>.png. The panel is a separate process, so
// it re-serves the same files through this controller.
[ApiController]
[Authorize(Policy = Permissions.ManagePlayers)]
[Route("api/persona-images")]
internal sealed class PersonaImagesController : ControllerBase
{
    // Published layout: the ApiServer is published into the components dir next
    // to the panel, thumbnails end up in its wwwroot. When running from the repo
    // (bin/Debug/net10.0), fall back to the ApiServer project directory instead.
    private static readonly string[] ImagesDirs =
    [
        Path.Combine(Program.ProgramsDir, "wwwroot", "playfab", "images"),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Apace.ApiServer", "wwwroot", "playfab", "images")),
    ];

    [HttpGet("{id}.png")]
    [HttpHead("{id}.png")]
    public static Results<PhysicalFileHttpResult, NotFound> GetThumbnail(string id)
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

        return TypedResults.NotFound();
    }
}
