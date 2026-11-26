using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Apace.ApiServer.Models;
using Apace.Common;

namespace Apace.ApiServer.Utils;

internal static class JwtUtils
{
    private static readonly JwtSecurityTokenHandler jwtHandler = new JwtSecurityTokenHandler();

    public static string Sign<TData>(Token<TData> token, byte[] secret)
        where TData : ITokenData<TData>
        => SignInternal<TData>(token, secret, new ValidityDatePair(token.Issued, token.Expires));

    public static string Sign<TData>(TData data, byte[] secret, ValidityDatePair validity)
        where TData : ITokenData<TData>
        => SignInternal<TData>(data, secret, validity);

    private static string SignInternal<TData>(object dataOrToken, byte[] secret, ValidityDatePair validity)
        where TData : ITokenData<TData>
    {
        TData data = dataOrToken switch
        {
            Token<TData> token => token.Data,
            TData tokenData => tokenData,
            _ => throw new UnreachableException(),
        };

        Claim[] payload =
        [
            new Claim("iat", validity.Issued.ToUnixTimeSeconds().ToString()),
            new Claim("nbf", validity.Issued.ToUnixTimeSeconds().ToString()),
            new Claim("exp", validity.Expires.ToUnixTimeSeconds().ToString()),
            new Claim("data", Json.Serialize(data)),
        ];

        return jwtHandler.WriteToken(new JwtSecurityToken(
            new JwtHeader(new SigningCredentials(
                new SymmetricSecurityKey(secret),
                SecurityAlgorithms.HmacSha256)),
            new JwtPayload(payload)
        ));
    }

    public static Token<TData>? Verify<TData>(string token, byte[] secret, bool allowExpired = false)
        where TData : ITokenData<TData>
        => VerifyAny<TData>(token, [secret], allowExpired);

    /// <summary>
    /// Verifies a token against several candidate secrets (the current one first).
    /// Used for tokens clients cache across server updates: the first secret is
    /// the installation's own, the remaining ones are legacy fallbacks.
    /// </summary>
    public static Token<TData>? VerifyAny<TData>(string token, IReadOnlyList<byte[]> secrets, bool allowExpired = false)
        where TData : ITokenData<TData>
    {
        // an absent token is a normal case (e.g. an RST2 request without a device token), not a verification failure
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        for (int i = 0; i < secrets.Count; i++)
        {
            var verified = VerifyWithSecret<TData>(token, secrets[i], allowExpired);

            if (verified is not null)
            {
                if (i > 0)
                {
                    Log.Warning("JWT verified with a legacy token secret — a client still uses tokens cached before the update " +
                                "(turn off with AcceptLegacyTokenSecrets=false in api_config.json once nobody needs it)");
                }

                return verified;
            }
        }

        return null;
    }

    private static Token<TData>? VerifyWithSecret<TData>(string token, byte[] secret, bool allowExpired)
        where TData : ITokenData<TData>
    {
        try
        {
            var claims = jwtHandler.ValidateToken(token, new TokenValidationParameters()
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = !allowExpired,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(secret),
            }, out _).Claims.ToDictionary(claim => claim.Type, claim => claim.Value);

            if (!claims.TryGetValue("iat", out string? iat) || !claims.TryGetValue("exp", out string? exp) || !claims.TryGetValue("data", out string? dataJson))
            {
                return null;
            }

            if (!long.TryParse(iat, out long issuedSeconds) || !long.TryParse(exp, out long expiresSeconds))
            {
                return null;
            }

            var expires = DateTimeOffset.FromUnixTimeSeconds(expiresSeconds);

            var data = Json.Deserialize<TData>(dataJson);
            if (data is null)
            {
                return null;
            }

            return new Token<TData>(DateTimeOffset.FromUnixTimeSeconds(issuedSeconds), expires, allowExpired && expires < DateTimeOffset.UtcNow, data);
        }
        catch (SecurityTokenException ex)
        {
            Log.Debug($"JWT verification failed: {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            Log.Debug($"JWT data deserialization failed: {ex.Message}");
            return null;
        }
        // Malformed tokens (not three segments, bad base64, unparsable payload) surface as
        // ArgumentException/FormatException from the handler's own parsing, and some validation
        // edge cases as InvalidOperationException. A token is UNTRUSTED INPUT: verification
        // must never leak an unhandled exception to the caller — before this catch set a
        // garbage token (e.g. a client replaying a cached-but-corrupt code at
        // login.live.com/oauth20_token.srf) killed the endpoint with an EMPTY 500 instead
        // of the 400 invalid_grant the OAuth contract promises, which XAL surfaces as a hard
        // sign-in failure instead of a clean re-auth.
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            Log.Debug($"JWT verification failed (malformed token): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
