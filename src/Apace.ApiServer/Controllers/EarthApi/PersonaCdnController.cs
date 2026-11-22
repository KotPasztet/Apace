using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Apace.ApiServer.Controllers.EarthApi;

// Serves persona piece binaries (personabinary downloads referenced by PersonaDurable
// catalog items) — preferred source is the persistent Mojang assets dir in the data
// volume (filled by the panel's asset gate), with a fallback to the legacy
// staticdata/playfab/persona/ for installs that predate the gate. Files can be
// managed through the panel's file browser or dropped in manually; only simple
// file names are accepted.
[Route("playfab/persona/{fileName}")]
public class PersonaCdnController : ControllerBase
{
    [HttpGet]
    public Results<NotFound, FileStreamHttpResult> Get(string fileName)
    {
        // the route value must be a plain file name: reject anything that tries to
        // escape the persona directory (slashes, "..", rooted paths, ...)
        if (Path.GetFileName(fileName) != fileName || fileName.Contains(".."))
        {
            return TypedResults.NotFound();
        }

        string filePath = Path.Combine(Program.MojangAssetsDir, "playfab", "persona", fileName);
        if (!System.IO.File.Exists(filePath))
        {
            string directory = Path.GetFullPath(Path.Combine(Program.staticData.Directory, "playfab", "persona"));
            filePath = Path.Combine(directory, fileName);
        }

        if (!System.IO.File.Exists(filePath))
        {
            return TypedResults.NotFound();
        }

        Stream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return TypedResults.Stream(stream, "application/zip", fileDownloadName: fileName);
    }
}
