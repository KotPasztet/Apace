using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Apace.ApiServer.Controllers.EarthApi;

// Serves classic skin pack binaries (skinbinary downloads referenced by
// MarketplaceDurableCatalog_V1.2 skin pack offers) from
// staticdata/playfab/skinpacks/, mirroring PersonaCdnController. Files can be
// managed through the panel's file browser or dropped in manually; only simple
// file names are accepted.
[Route("playfab/skinpacks/{fileName}")]
public class SkinPackCdnController : ControllerBase
{
    [HttpGet]
    public Results<NotFound, FileStreamHttpResult> Get(string fileName)
    {
        string directory = Path.GetFullPath(Path.Combine(Program.staticData.Directory, "playfab", "skinpacks"));

        // the route value must be a plain file name: reject anything that tries to
        // escape the skinpacks directory (slashes, "..", rooted paths, ...)
        if (Path.GetFileName(fileName) != fileName || fileName.Contains(".."))
        {
            return TypedResults.NotFound();
        }

        string filePath = Path.Combine(directory, fileName);
        if (!System.IO.File.Exists(filePath))
        {
            return TypedResults.NotFound();
        }

        Stream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return TypedResults.Stream(stream, "application/zip", fileDownloadName: fileName);
    }
}
