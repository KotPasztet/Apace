using Apace.LauncherUI.Data;
using Microsoft.AspNetCore.Identity;

namespace Apace.LauncherUI.Utils;

// WHY: cookie sessions survive panel restarts, so the login-page grant never runs for
// already-signed-in users; this closes the last gap — the first authenticated request
// after an update grants Owner when no admin exists. WebApplication inserts the
// implicit UseAuthentication before user middleware, so context.User is already
// populated by the time this runs.
public sealed class OwnerBootstrapMiddleware
{
    // WHY: the "does an owner exist?" check behind EnsureOwnerForUserAsync is a DB query
    // (GetUsersInRoleAsync), so it must not run on every request. One process-wide fact
    // is enough and the flag is never reset: once an owner exists it survives panel
    // updates, and the startup self-heal + login-page grants cover anything this
    // middleware misses. Steady-state cost per request is a single volatile read.
    private static volatile bool _ownerVerified;

    private readonly RequestDelegate _next;

    public OwnerBootstrapMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    // WHY: AddIdentityCore registers UserManager<ApplicationUser> as scoped, so the
    // InvokeAsync convention resolves it from the request scope — no manual scope.
    public async Task InvokeAsync(HttpContext context, UserManager<ApplicationUser> userManager, ILogger<OwnerBootstrapMiddleware> logger)
    {
        if (!_ownerVerified && context.User.Identity?.IsAuthenticated == true)
        {
            try
            {
                // WHY: resolve the ApplicationUser behind the (possibly persisted-cookie)
                // principal. Null means a stale identity, e.g. a deleted user — do nothing
                // and leave the flag unset so a later request can still verify.
                var user = await userManager.GetUserAsync(context.User);
                if (user is not null)
                {
                    // WHY: delegate, don't duplicate — the Gate-serialized check-and-grant
                    // lives in OwnerBootstrap alone. true = granted here or an owner
                    // already existed; false = the grant failed (logged there), so the
                    // flag stays unset and the next authenticated request retries.
                    if (await OwnerBootstrap.EnsureOwnerForUserAsync(userManager, user, logger))
                    {
                        _ownerVerified = true;
                    }
                }
            }
            catch (Exception ex)
            {
                // WHY: a failed bootstrap probe must never break the request; the flag
                // stays unset, so the next authenticated request retries.
                logger.LogWarning(ex, "Owner bootstrap: probe failed, will retry on the next request.");
            }
        }

        await _next(context);
    }
}
