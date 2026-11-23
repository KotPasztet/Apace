using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Apace.ApiServer.Services;
using CItem = Apace.StaticData.Playfab.Item;

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

        // Pack gate for the frozen 2021 staticdata binaries, before the mirror and
        // staticdata fallback dirs are consulted: a zip named <itemId>.zip whose id
        // resolves to a staticdata PersonaData of a Minecraft-creator (or creatorless)
        // accessory downloads only through the same allowed set the live lazy path
        // gates against — staticdata persona items carry no pack tags, so no pack
        // selection ever covers them and the binary refuses whether the set is empty
        // (no packs selected) or not. Live-mirrored zips have no staticdata entry and
        // stay governed by the lazy-path gate below.
        string staticItemId = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
        if (Guid.TryParse(staticItemId, out Guid staticItemGuid)
            && Program.staticData.Playfab.Items.TryGetValue(staticItemGuid, out CItem? staticItem)
            && staticItem.Data is CItem.PersonaData staticPersona
            && staticPersona.CreatorName is null or "Minecraft"
            && staticPersona.PieceType is not "persona_skin")
        {
            IReadOnlySet<string> allowed = await PersonaPackGate.AllAllowedPackUuidsAsync(HttpContext.RequestAborted);
            LogPackGateRefusalOnce(fileName, allowed.Count == 0 ? "no packs selected" : "staticdata carries no pack tags — no pack selection covers it");
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

    private static readonly HashSet<string> PackGateRefusalsLogged = [];

    // One line per refused file per run (same dedup as
    // LivePersonaCatalog.LogPackGateRefusalOnce) — the client retries the same
    // binary, the refusal must not flood the log.
    private static void LogPackGateRefusalOnce(string fileName, string reason)
    {
        lock (PackGateRefusalsLogged)
        {
            if (!PackGateRefusalsLogged.Add(fileName))
            {
                return;
            }
        }

        Log.Information("Pack gate refuses staticdata accessory binary {File}: {Reason} — binaries of accessories outside the selected packs are never fetched", fileName, reason);
    }
}
