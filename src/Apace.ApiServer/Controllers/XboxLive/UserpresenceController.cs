using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Apace.ApiServer.Utils;
using System.Text.RegularExpressions;

namespace Apace.ApiServer.Controllers.XboxLive;

[Route("users")]
[Route("userpresence.xboxlive.com/users")]
internal sealed partial class UserpresenceController : ApaceControllerBase
{
    [HttpPost("{xuidParam}/devices/current/titles/current")]
    public Results<Ok, UnauthorizedHttpResult, BadRequest> GetTitles(string xuidParam)
    {
        var authUnion = XboxLiveAuth();
        if (authUnion.IsB)
        {
            return authUnion.B.Result is UnauthorizedHttpResult unauthorized ? unauthorized : (BadRequest)authUnion.B.Result;
        }

        var token = authUnion.A;

        Match xuidMatch = GetXuidRegex().Match(xuidParam);

        string? xuid = xuidMatch.Success ? xuidMatch.Groups[1].Value : null;

        if (xuid is null)
        {
            return TypedResults.BadRequest();
        }

        if (!XuidUtils.Matches(xuid, token.UserId))
        {
            return TypedResults.Unauthorized();
        }

        // TODO

        return TypedResults.Ok();
    }

    // GET /users/xuid(<id>)?level=all — the client's own-presence poll. Solace
    // 404s this one too, but a 404 here is the last red entry in a logged-in
    // session and the iOS client boots through a stricter service-check path,
    // so serve a minimal always-online presence instead of letting it 404.
    [HttpGet("{xuidParam}")]
    public Results<Ok<Dictionary<string, object?>>, UnauthorizedHttpResult, BadRequest> GetPresence(string xuidParam)
    {
        var authUnion = XboxLiveAuth();
        if (authUnion.IsB)
        {
            return authUnion.B.Result is UnauthorizedHttpResult unauthorized ? unauthorized : (BadRequest)authUnion.B.Result;
        }

        var token = authUnion.A;

        Match xuidMatch = GetXuidRegex().Match(xuidParam);

        string? xuid = xuidMatch.Success ? xuidMatch.Groups[1].Value : null;

        if (xuid is null)
        {
            return TypedResults.BadRequest();
        }

        if (!XuidUtils.Matches(xuid, token.UserId))
        {
            return TypedResults.Unauthorized();
        }

        return TypedResults.Ok(new Dictionary<string, object?>
        {
            ["xuid"] = xuid,
            ["state"] = "Online",
            ["devices"] = Array.Empty<object>(),
        });
    }

    // TODO

    [GeneratedRegex(@"^xuid\((.*)\)$")]
    private static partial Regex GetXuidRegex();
}
