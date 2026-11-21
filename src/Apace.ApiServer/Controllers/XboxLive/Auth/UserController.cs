using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Apace.ApiServer.Models;
using Apace.ApiServer.Utils;

namespace Apace.ApiServer.Controllers.XboxLive.Auth;

[Route("user/authenticate")]
[Route("user.auth.xboxlive.com/user/authenticate")]
internal sealed class UserController : ApaceControllerBase
{
    private static Config config => Program.config;

    public sealed record AuthenticateRequest(
        AuthenticateRequest.PropertiesR Properties,
        string RelyingParty,
        string TokenType
    )
    {
        public sealed record PropertiesR(
            string AuthMethod,
            string RpsTicket,
            string SiteName
        );
    }

    private sealed record AuthenticateResponse(
        string IssueInstant,
        string NotAfter,
        string Token,
        Dictionary<string, Dictionary<string, string>[]> DisplayClaims
    );

    [HttpPost]
    public Results<ContentHttpResult, UnauthorizedHttpResult> Authenticate([FromBody] AuthenticateRequest request)
    {
        // Tickets cached by clients before the switch to random per-installation
        // secrets are signed with the legacy public Solace secret; without the
        // fallback every such client is locked out of the account login in a
        // 401 loop (the client never re-requests an unexpired ticket).
        var ticket = JwtUtils.VerifyAny<Tokens.Shared.XboxTicketToken>(
            request.Properties.RpsTicket,
            config.VerificationSecrets(config.Login.XboxTokenSecretBytes, LegacyTokenSecrets.XboxTokenSecretBytes))?.Data;

        if (ticket is null)
        {
            return TypedResults.Unauthorized();
        }

        var tokenValidity = ValidityDatePair.Create(config.XboxLive.TokenValidityMinutes);
        var token = new Tokens.Xbox.UserToken()
        {
            Xid = ticket.UserId,
            Uhs = ticket.UserId,

            UserId = ticket.UserId,
            Username = ticket.Username,
        };

        return JsonPascalCase(new AuthenticateResponse(
            tokenValidity.IssuedStr,
            tokenValidity.ExpiresStr,
            JwtUtils.Sign<Tokens.Xbox.AuthToken>(token, config.XboxLive.AuthTokenSecretBytes, tokenValidity),
            new()
            {
                ["xui"] = [
                    new()
                    {
                        ["uhs"] = token.Uhs,
                    },
                ],
            }
        ));
    }
}
