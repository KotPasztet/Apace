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
// live CDN URL: every offer advertises this proxy path. The live fetch requires
// entitlement (a pack selection or the blank-slot filler pool — the catalog offer
// governs visibility), never the bare id.
//
// Every request resolves: the frozen client RETRIES a failed piece download until
// its own timeout, so a 404 leaves the player stuck on "loading" — an unavailable
// piece gets the deterministic placeholder zip (PersonaStubBinary) instead, whose
// import succeeds instantly and renders nothing.
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

        string itemId = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
        bool isGuidName = Guid.TryParse(itemId, out Guid itemGuid);

        // Disk first: a zip sitting on the volume serves whoever asks — the file
        // being mirrored means it was served before, and the equip-side sanitize
        // governs entitlement. The old gate-first order made pieces a player had
        // already seen 404 on equip (stale client caches, a pack since
        // deselected, a piece the limit later cut) — every one of those was a
        // stuck "loading" screen.
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
        if (!System.IO.File.Exists(filePath) && isGuidName && LivePersonaCatalog.Enabled
            && await CanServeLiveBinaryAsync(itemGuid, HttpContext.RequestAborted))
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

        if (!System.IO.File.Exists(filePath))
        {
            // the client hangs on a failed piece download — serve the placeholder
            // instead of a 404 (guid-named binaries only; panel-managed static
            // files keep their plain 404)
            if (isGuidName)
            {
                return PlaceholderZip(itemGuid, fileName);
            }

            return TypedResults.NotFound();
        }

        Stream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return TypedResults.Stream(stream, "application/zip", fileDownloadName: fileName);
    }

    // Entitlement for a LIVE fetch: the piece must be covered by a pack
    // selection (defaults ∪ every player's grants — this endpoint has no caller
    // identity) or by the blank-slot filler pool, or not be a gated accessory
    // at all (skins, emotes, unknown ids serve as they always did). Staticdata
    // Minecraft accessories carry no pack tags — no selection ever covers them,
    // so their mirror dirs are the only source and a missing file gets the
    // placeholder.
    private static async Task<bool> CanServeLiveBinaryAsync(Guid itemGuid, CancellationToken cancellationToken)
    {
        if (Program.staticData.Playfab.Items.TryGetValue(itemGuid, out CItem? staticItem)
            && staticItem.Data is CItem.PersonaData staticPersona
            && staticPersona.CreatorName is null or "Minecraft"
            && staticPersona.PieceType is not "persona_skin")
        {
            return false;
        }

        if (Controllers.PlayfabApi.CatalogController.FillerItemIds.Contains(itemGuid.ToString()))
        {
            return true;
        }

        if (LivePersonaCatalog.GetIndexEntry(itemGuid.ToString()) is { } entry
            && entry.PieceType is not "persona_skin"
            && entry.PieceType.StartsWith("persona_", StringComparison.Ordinal))
        {
            IReadOnlySet<string> allowed = await PersonaPackGate.AllAllowedPackUuidsAsync(cancellationToken);
            return allowed.Count > 0 && LivePersonaCatalog.ThemeTagsOf(entry).Any(allowed.Contains);
        }

        return true;
    }

    // The deterministic placeholder piece: piece uuid + type from whatever
    // metadata carries them (the live index first, then staticdata, then the
    // panel's custom cosmetics) — the ONE uuid the pack must use is the
    // catalog item's PieceId, so the client imports the placeholder as exactly
    // the piece it asked for.
    private static FileStreamHttpResult? PlaceholderZip(Guid itemGuid, string fileName)
    {
        string pieceId = itemGuid.ToString();
        string pieceType = "";
        if (LivePersonaCatalog.GetIndexEntry(itemGuid.ToString()) is { } entry)
        {
            pieceId = entry.PieceId.ToString();
            pieceType = entry.PieceType;
        }
        else if (Program.staticData.Playfab.Items.TryGetValue(itemGuid, out CItem? staticItem)
            && staticItem.Data is CItem.PersonaData persona)
        {
            pieceId = persona.PieceId.ToString();
            pieceType = persona.PieceType;
        }
        else
        {
            var custom = CustomCosmetics.Items.FirstOrDefault(candidate => candidate.Id.Equals(itemGuid.ToString(), StringComparison.OrdinalIgnoreCase));
            if (custom is not null)
            {
                pieceId = custom.PieceId;
                pieceType = custom.PieceType;
            }
        }

        // the ContentIdentity the stub advertises MUST match what the catalog
        // promised (packIdentity PieceId) and MUST have a receipt entitlement —
        // log both facts per serve; an unindexed id (fallback to the raw item
        // guid) can never equip, even with receipts
        Log.Information(
            "Placeholder {File}: ContentIdentity {PieceId}, piece type {PieceType}{Unindexed}",
            fileName, pieceId, string.IsNullOrWhiteSpace(pieceType) ? "?" : pieceType,
            pieceId == itemGuid.ToString()
                ? " — UNINDEXED id: no live-index/staticdata/custom metadata resolved, the client cannot equip this"
                : "");
        LogPlaceholderOnce(fileName);
        return TypedResults.Stream(new MemoryStream(PersonaStubBinary.Build(pieceId, pieceType)), "application/zip", fileDownloadName: fileName);
    }

    private static readonly HashSet<string> PlaceholdersLogged = [];

    // One line per placeholder per run (the client retries the same binary until
    // its timeout succeeds — the first attempt is the only interesting one).
    private static void LogPlaceholderOnce(string fileName)
    {
        lock (PlaceholdersLogged)
        {
            if (!PlaceholdersLogged.Add(fileName))
            {
                return;
            }
        }

        Log.Information("Persona zip {File} is unavailable (probed dead, never mirrored or not entitled for a live fetch) — serving the deterministic placeholder piece so the equip completes instead of hanging", fileName);
    }
}
