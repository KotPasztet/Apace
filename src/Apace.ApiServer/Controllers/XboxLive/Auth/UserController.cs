using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Apace.ApiServer.Models;
using Apace.ApiServer.Utils;

namespace Apace.ApiServer.Controllers.XboxLive.Auth;

[Route("user/authenticate")]
[Route("user.auth.xboxlive.com/user/authenticate")]
internal sealed class UserController : ApaceControllerBase
{
    private static Config config => Program.config;

    private static bool warnedAboutUnknownSecretTicket;

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
            // A ticket that matches neither the installation's secrets nor the legacy public
            // Solace secrets was cached while the installation was still rotating its secrets
            // on every boot (the pre-fix api_config.json / dataprotection-keys locations): no
            // server can verify it, and the client keeps replaying it in a 401 loop instead of
            // falling back to the login page. The only way out for such a client is one full
            // sign-in through the login page (or clearing the app's data).
            if (!warnedAboutUnknownSecretTicket)
            {
                warnedAboutUnknownSecretTicket = true;
                Log.Warning("user/authenticate rejected a ticket signed with unknown secrets (cached before the login secrets " +
                            "were persisted across redeploys); that client must sign in once through the login page " +
                            "(warning logged once per restart, further occurrences suppressed)");
            }

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
