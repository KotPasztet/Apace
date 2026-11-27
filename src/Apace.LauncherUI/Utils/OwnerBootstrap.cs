using Microsoft.AspNetCore.Identity;
using Apace.LauncherUI.Data;

namespace Apace.LauncherUI.Utils;

// WHY: the first-account owner grant is shared by both registration paths (password
// and external login). The gate and the check must live in ONE place — a per-page
// static SemaphoreSlim would compile to two independent locks and serialize nothing.
public static class OwnerBootstrap
{
    // WHY: serializes the "does any owner exist?" check + grant so two concurrent
    // registrations cannot both pass the check and end up with two owners.
    public static readonly SemaphoreSlim Gate = new(1, 1);

    // WHY: decides by role membership, not by "users table is empty" — a stale row
    // (shared Data/app.db between channels, external-login account) must never block
    // the grant forever; and external signup gets the grant too, not just password
    // registration. Failure to add the role is logged, not silently ignored.
    public static async Task EnsureOwnerAfterCreateAsync(UserManager<ApplicationUser> userManager, ApplicationUser user, ILogger logger)
    {
        await Gate.WaitAsync();
        try
        {
            var hasOwner = (await userManager.GetUsersInRoleAsync(ApplicationRole.Owner)).Any();
            if (hasOwner)
            {
                return;
            }

            var add = await userManager.AddToRoleAsync(user, ApplicationRole.Owner);
            if (!add.Succeeded)
            {
                // WHY: don't fail the registration itself — the self-heal at startup
                // covers a missing owner; but leave a trace so it stays diagnosable.
                logger.LogWarning("Owner bootstrap: failed to grant owner role to '{UserName}': {Errors}",
                    user.UserName, string.Join(", ", add.Errors.Select(e => e.Description)));
            }
            else
            {
                logger.LogInformation("Owner bootstrap: granted owner role to '{UserName}' (no admin existed).",
                    user.UserName);
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
