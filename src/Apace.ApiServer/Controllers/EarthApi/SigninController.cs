using Asp.Versioning;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using System.Text.RegularExpressions;
using Apace.ApiServer.Authentication;
using Apace.ApiServer.Models;
using Apace.ApiServer.Utils;
using Apace.Common.Utils;

namespace Apace.ApiServer.Controllers;

[ApiVersion("1.1")]
internal sealed partial class SigninController : ApaceControllerBase
{
    private const double SessionTokenValidityHours = 12; // TODO: make configurable

    private static Config config => Program.config;

    private readonly ITimeLimitedDataProtector _protector;

    public SigninController(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(GenoaAuthenticationHandler.DataProtectionPurpose).ToTimeLimitedDataProtector();
    }

    [GeneratedRegex("^[0-9A-F]{15,16}$")]
    private static partial Regex GetUserIdRegex();

    [HttpPost("api/v{version:apiVersion}/player/profile/{profileID}")]
    [HttpPost("1/api/v{version:apiVersion}/player/profile/{profileID}")]
    public async Task<Results<ContentHttpResult, BadRequest>> Post(string profileID, CancellationToken cancellationToken)
    {
        if (profileID != "signin")
        {
            return TypedResults.BadRequest();
        }

        SigninRequest? signinRequest = await Request.Body.AsJsonAsync<SigninRequest>(cancellationToken);

        string[]? parts = null;
        if (signinRequest is null || (parts = signinRequest.SessionTicket.Split('-')).Length < 2)
        {
            Log.Error($"Sign in request null or parts bad ({parts?.Length ?? -1})");
            return TypedResults.BadRequest();
        }

        string userId = parts[0];
        if (!GetUserIdRegex().IsMatch(userId))
        {
            Log.Error($"User id not match ({userId})");
            return TypedResults.BadRequest();
        }

        if (Program.LocalLoginOnly)
        {
            // self-asserted session tickets cannot be verified, only accept tickets issued by this server (local accounts)
            string jwt = string.Join('-', parts, 1, parts.Length - 1);
            var sessionTicket = JwtUtils.Verify<Tokens.Shared.PlayfabSessionTicket>(jwt, config.PlayfabApi.SessionTicketSecretBytes);

            if (sessionTicket is null)
            {
                Log.Warning($"Sign in - local login only is enabled and the session ticket was not issued by this server");
                return TypedResults.BadRequest();
            }

            if (!string.Equals(sessionTicket.Data.UserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning($"Sign in - local login only is enabled and the session ticket user id does not match ({sessionTicket.Data.UserId} != {userId})");
                return TypedResults.BadRequest();
            }
        }

        // TODO: check credentials

        await TokenUtils.EnsureDailyLoginToken(userId.ToLowerInvariant(), cancellationToken);

        // the session token is a data-protected, time-limited wrapper around the user id
        // (verified by GenoaAuthenticationHandler) instead of the raw, forgeable user id
        string token = _protector.Protect(userId.ToUpperInvariant(), TimeSpan.FromHours(SessionTokenValidityHours));

        return EarthJson(new Dictionary<string, object?>()
        {
            ["authenticationToken"] = token,
            ["basePath"] = "/1",
            ["clientProperties"] = new object(),
            ["mixedReality"] = null,
            ["mrToken"] = null,
            ["streams"] = null,
            ["tokens"] = new object(),
            ["updates"] = new object(),
        });
    }

    private sealed record SigninRequest(string SessionTicket);
}
