using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Apace.ApiServer.Controllers.EarthApi;

// Serves classic skin pack binaries (skinbinary downloads referenced by
// MarketplaceDurableCatalog_V1.2 skin pack offers) — preferred source is the
// persistent Mojang assets dir in the data volume, with a fallback to
// staticdata/playfab/skinpacks/ (mirroring PersonaCdnController). Files can be
// managed through the panel's file browser or dropped in manually; only simple
// file names are accepted.
[Route("playfab/skinpacks/{fileName}")]
public class SkinPackCdnController : ControllerBase
{
    [HttpGet]
    public Results<NotFound, FileStreamHttpResult> Get(string fileName)
    {
        // the route value must be a plain file name: reject anything that tries to
        // escape the skinpacks directory (slashes, "..", rooted paths, ...)
        if (Path.GetFileName(fileName) != fileName || fileName.Contains(".."))
        {
            return TypedResults.NotFound();
        }

        string filePath = Path.Combine(Program.MojangAssetsDir, "playfab", "skinpacks", fileName);
        if (!System.IO.File.Exists(filePath))
        {
            string directory = Path.GetFullPath(Path.Combine(Program.staticData.Directory, "playfab", "skinpacks"));
            filePath = Path.Combine(directory, fileName);
        }

        if (!System.IO.File.Exists(filePath))
        {
            // a 404 here means the client wants a pack whose zip the volume never
            // got (builder did not run / assets incomplete) — make it visible,
            // these used to vanish as INF noise in the access log
            Log.Warning("Skin pack zip not found on this install: {FileName} (looked in the data volume and staticdata)", fileName);
            return TypedResults.NotFound();
        }

        Stream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return TypedResults.Stream(stream, "application/zip", fileDownloadName: fileName);
    }
}
