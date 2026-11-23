using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Apace.ApiServer.Services;

namespace Apace.ApiServer.Controllers.EarthApi;

// Serves persona piece binaries (personabinary downloads referenced by PersonaDurable
// catalog items) — preferred source is the persistent Mojang assets dir in the data
// volume, with a fallback to the legacy
// staticdata/playfab/persona/ for older installs. Files can be
// managed through the panel's file browser or dropped in manually; only simple
// file names are accepted.
//
// Live persona marketplace: when the item is NOT in either dir but the live catalog
// has it, the controller runs the lazy pipeline (Services/LivePersonaCatalog) —
// resolve the real CDN URL via Catalog/GetItem, probe it (the new PlayFab content
// CDN serves the same GUIDs the dark xforge CDN once did), download the piece into
// <mojang-assets>/persona-catalog/binaries/ and serve it. The client never sees a
// live CDN URL: every offer advertises this proxy path.
[Route("playfab/persona/{fileName}")]
public class PersonaCdnController : ControllerBase
{
    [HttpGet]
    public async Task<Results<NotFound, FileStreamHttpResult>> Get(string fileName)
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
            if (System.IO.File.Exists(filePath))
            {
                // cape diagnostics: which copy answered — the data volume or the legacy staticdata tree
                Log.Debug("Persona zip {FileName} resolved from the staticdata fallback ({Path})", fileName, filePath);
            }
        }

        // live persona marketplace: a miss on both dirs is the lazy probe + download
        // path for items the panel synced from the live marketplace
        if (!System.IO.File.Exists(filePath) && LivePersonaCatalog.Enabled)
        {
            // <itemId>.zip — the same name the offer's Contents entry advertises
            string itemId = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^4]
                : fileName;
            if (Guid.TryParse(itemId, out _))
            {
                try
                {
                    if (await LivePersonaCatalog.TryEnsureBinaryAsync(itemId, Log.Logger, HttpContext.RequestAborted))
                    {
                        filePath = LivePersonaCatalog.BinaryPath(itemId);
                    }
                }
                finally
                {
                    // burst-friendly: persist probe verdicts after each request
                    LivePersonaCatalog.FlushProbeStates(Log.Logger);
                }
            }
        }

        if (!System.IO.File.Exists(filePath))
        {
            return TypedResults.NotFound();
        }

        Stream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return TypedResults.Stream(stream, "application/zip", fileDownloadName: fileName);
    }
}
