using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Apace.ApiServer.Controllers.EarthApi;

// Serves the additional Mojang asset thumbnails (dressing room item images) from
// the persistent Mojang assets dir in the data volume. The wwwroot copies are
// served earlier by UseStaticFiles when they exist — this controller is the
// data-volume source for installs where the repo no longer ships the images
// (the asset gate downloads them to <data>/mojang-assets/images/).
// Only simple file names are accepted.
[Route("playfab/images/{fileName}")]
public class MojangImagesCdnController : ControllerBase
{
    [HttpGet]
    public Results<NotFound, FileStreamHttpResult> Get(string fileName)
    {
        // the route value must be a plain file name: reject anything that tries to
        // escape the images directory (slashes, "..", rooted paths, ...)
        if (Path.GetFileName(fileName) != fileName || fileName.Contains(".."))
        {
            return TypedResults.NotFound();
        }

        string directory = Path.Combine(Program.MojangAssetsDir, "images");
        string filePath = Path.Combine(directory, fileName);
        if (!System.IO.File.Exists(filePath))
        {
            return TypedResults.NotFound();
        }

        string contentType = Path.GetExtension(fileName).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(fileName).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg"
            : "image/png";

        Stream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return TypedResults.Stream(stream, contentType, fileDownloadName: fileName);
    }
}
