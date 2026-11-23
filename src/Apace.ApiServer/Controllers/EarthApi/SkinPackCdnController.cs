using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Apace.ApiServer.Services;
using CItem = Apace.StaticData.Playfab.Item;

namespace Apace.ApiServer.Controllers.EarthApi;

// Serves classic skin pack binaries (skinbinary downloads referenced by
// MarketplaceDurableCatalog_V1.2 skin pack offers) — preferred source is the
// persistent Mojang assets dir in the data volume, with a fallback to
// staticdata/playfab/skinpacks/ (mirroring PersonaCdnController). Files can be
// managed through the panel's file browser or dropped in manually; only simple
// file names are accepted.
//
// Live persona marketplace: when the zip is NOT in either dir and the live catalog
// has the pack, the controller runs the lazy pipeline (Services/LivePersonaCatalog)
// — resolve the real CDN URL via Catalog/GetItem, probe it (the new PlayFab content
// CDN serves the same GUIDs the dark xforge CDN once did), download the pack into
// <mojang-assets>/playfab/skinpacks/ and serve it. The client never sees a live CDN
// URL: every skin pack offer advertises this proxy path.
[Route("playfab/skinpacks/{fileName}")]
public class SkinPackCdnController : ControllerBase
{
    [HttpGet]
    public async Task<Results<NotFound, FileStreamHttpResult>> Get(string fileName)
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
            if (System.IO.File.Exists(filePath))
            {
                // pack diagnostics: which copy answered — the data volume or the legacy staticdata tree
                Log.Debug("Skin pack zip {FileName} resolved from the staticdata fallback ({Path})", fileName, filePath);
            }
        }

        // live persona marketplace: a miss on both dirs is the lazy resolve + download
        // path for packs the panel synced from the live marketplace ("Minecraft adult
        // skins" and friends — the offer's skinbinary Contents entry advertises exactly
        // this <offerId>.zip name)
        if (!System.IO.File.Exists(filePath) && LivePersonaCatalog.Enabled)
        {
            // <offerId>.zip — the same name the offer's Contents entry advertises
            string offerId = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^4]
                : fileName;
            if (Guid.TryParse(offerId, out _))
            {
                await LivePersonaCatalog.TryEnsureSkinPackZipAsync(offerId, Log.Logger, HttpContext.RequestAborted);
                filePath = LivePersonaCatalog.SkinPackZipPath(offerId);
            }
        }

        if (!System.IO.File.Exists(filePath))
        {
            // a 404 here means the client wants a pack whose zip the volume never
            // got (builder did not run / assets incomplete / the live pack probed
            // dead upstream) — make it visible, these used to vanish as INF noise
            // in the access log
            if (IsKnownStaticDataSkinPackOffer(fileName, out string offerId))
            {
                // a known staticdata skin pack offer with no zip anywhere — the
                // dressing room keeps rendering the offer (empty rows) instead of
                // hiding it; for the built-in "Minecraft Default Skins" offer the
                // fix is the default-skins texture fetch at panel start
                // (MojangSkinTextureFetcher → MojangSkinPackBuilder)
                Log.Warning(
                    "Skin pack zip {FileName} of staticdata offer {OfferId} not found at {ExpectedPath} (nor in staticdata) — the client shows this pack's rows empty; for the built-in \"Minecraft Default Skins\" offer the default-skins texture fetch (MojangSkinTextureFetcher, panel start) must land the skins/*.png textures before MojangSkinPackBuilder can build the zip",
                    fileName, offerId, Path.Combine(Program.MojangAssetsDir, "playfab", "skinpacks", fileName));
            }
            else
            {
                Log.Warning("Skin pack zip not found on this install: {FileName} (looked in the data volume and staticdata)", fileName);
            }

            return TypedResults.NotFound();
        }

        Stream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return TypedResults.Stream(stream, "application/zip", fileDownloadName: fileName);
    }

    // True when <fileName> (an <offerId>.zip) belongs to a staticdata skin pack offer
    // (the CatalogController.SkinPackOfferIds set: MarketplaceDurableCatalog_V1.2 items
    // tagged "has_skinpack") — those 404s deserve a loud diagnostic, because the
    // dressing room keeps showing such an offer with empty rows instead of hiding it.
    private static bool IsKnownStaticDataSkinPackOffer(string fileName, out string offerId)
    {
        offerId = Path.GetFileNameWithoutExtension(fileName);
        return Guid.TryParse(offerId, out var id)
            && Program.staticData.Playfab.Items.TryGetValue(id, out var item)
            && item.Data is CItem.ClientConfigData { ContentType: "MarketplaceDurableCatalog_V1.2" }
            && item.Tags.Contains("has_skinpack");
    }
}
