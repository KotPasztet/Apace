using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Solace.ApiServer.Utils;

namespace Solace.ApiServer.Controllers.EarthApi;

[ApiVersion("1.1")]
[Route("cdn/tile/16/{_}/{tilePos1}_{tilePos2}_16.png")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
internal sealed class CdnTileController : SolaceControllerBase
{
    [HttpGet]
    public async Task<Results<EmptyHttpResult, NotFound>> GetTile(int _, int tilePos1, int tilePos2, CancellationToken cancellationToken) // _ used because we dont care :|
    {
        Response.Headers.ContentType = "image/png";

        // Fast path: tiles are cached in memory, so an If-None-Match hit is answered
        // with 304 immediately, without touching the event bus / tile renderer.
        string? ifNoneMatch = Request.Headers.IfNoneMatch;
        TileUtils.CachedTileHit? cached = TileUtils.LookupCachedTile(tilePos1, tilePos2);
        if (cached is not null && cached.Value.ETag == ifNoneMatch)
        {
            Response.StatusCode = StatusCodes.Status304NotModified;
            Response.Headers.ETag = cached.Value.ETag;
            Response.ContentLength = 0;
            return TypedResults.Empty;
        }

        (string etag, byte[] tilePng) = cached is not null
            ? (cached.Value.ETag, cached.Value.Png)
            : await TileUtils.GetTileAsync(tilePos1, tilePos2, cancellationToken);

        Response.Headers.ETag = etag;
        Response.ContentLength = tilePng.Length;
        await Response.Body.WriteAsync(tilePng, cancellationToken);

        var cd = new System.Net.Mime.ContentDisposition { FileName = tilePos1 + "_" + tilePos2 + "_16.png", Inline = true };
        Response.Headers.Append("Content-Disposition", cd.ToString());

        return TypedResults.Empty;
    }
}
