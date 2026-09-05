using Asp.Versioning;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Solace.ApiServer.Utils;

namespace Solace.ApiServer.Controllers.EarthApi;

[ApiVersion("1.1")]
[Route("cdn/tile/16/{_}/{tilePos1}_{tilePos2}_16.png")]
[ResponseCache(Duration = 86400)]
internal sealed class CdnTileController : SolaceControllerBase
{
    [HttpGet]
    public async Task<Results<EmptyHttpResult, NotFound>> GetTile(int _, int tilePos1, int tilePos2, CancellationToken cancellationToken) // _ used because we dont care :|
    {
        // Fast path: tiles are immutable, so an If-None-Match hit is answered with 304
        // straight from the in-memory cache — no event-bus render, no body.
        TileUtils.CachedTileHit? cached = TileUtils.LookupCachedTile(tilePos1, tilePos2);
        if (cached is not null && cached.Value.ETag == Request.Headers.IfNoneMatch)
        {
            Response.StatusCode = StatusCodes.Status304NotModified;
            Response.Headers.ETag = cached.Value.ETag;
            return TypedResults.Empty;
        }

        (string etag, byte[] tilePng) = cached is not null
            ? (cached.Value.ETag, cached.Value.Png)
            : await TileUtils.GetTileAsync(tilePos1, tilePos2, cancellationToken);

        Response.Headers.ContentType = "image/png";
        Response.Headers.ETag = etag;
        Response.ContentLength = tilePng.Length;
        await Response.Body.WriteAsync(tilePng, cancellationToken);

        var cd = new System.Net.Mime.ContentDisposition { FileName = tilePos1 + "_" + tilePos2 + "_16.png", Inline = true };
        Response.Headers.Append("Content-Disposition", cd.ToString());

        return TypedResults.Empty;
    }
}
