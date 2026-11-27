using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace Apace.LauncherUI.Utils;

/// <summary>
/// Refreshes "Permission" and role claims from the database on every request.
/// This ensures newly auto-seeded permissions and role grants take effect immediately
/// without requiring the user to log out and back in.
/// </summary>
public sealed class PermissionClaimsTransformation : IClaimsTransformation
{
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly UserManager<Data.ApplicationUser> _userManager;

    public PermissionClaimsTransformation(
        RoleManager<ApplicationRole> roleManager,
        UserManager<Data.ApplicationUser> userManager)
    {
        _roleManager = roleManager;
        _userManager = userManager;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true })
        {
            return principal;
        }

        // Get the user
        var user = await _userManager.GetUserAsync(principal);
        if (user is null)
        {
            return principal;
        }

        var roleNames = await _userManager.GetRolesAsync(user);

        // Build fresh set of permission claims from role claims in the DB
        var freshPermissions = new HashSet<string>();
        foreach (var roleName in roleNames)
        {
            var role = await _roleManager.FindByNameAsync(roleName);
            if (role is null) continue;

            var claims = await _roleManager.GetClaimsAsync(role);
            foreach (var claim in claims)
            {
                if (claim.Type == "Permission")
                {
                    freshPermissions.Add(claim.Value);
                }
            }
        }

        // Replace existing claims with fresh ones
        var identity = principal.Identities.FirstOrDefault(i => i.IsAuthenticated);
        if (identity is null)
        {
            return principal;
        }

        // Remove stale permission claims
        var stalePermissions = identity.Claims
            .Where(c => c.Type == "Permission")
            .ToList();
        foreach (var stale in stalePermissions)
        {
            identity.TryRemoveClaim(stale);
        }

        // Add fresh permission claims
        foreach (var permission in freshPermissions)
        {
            identity.AddClaim(new Claim("Permission", permission));
        }

        // WHY: grants that happen after the cookie was issued (bootstrap at login /
        // middleware) must be visible without a re-login; Permission claims were
        // already refreshed — roles were not, which left role MANAGEMENT dead for a
        // bootstrapped owner (Users.razor / RoleManagement.razor derive
        // _userMinPosition from ClaimTypes.Role, which stayed at the stale cookie value).
        // NOTE: this sync must run even when there are no permission claims or no roles
        // at all — a role with zero Permission claims (or a fully revoked user) still
        // needs its stale role claims replaced/removed.
        var staleRoleClaims = identity.Claims
            .Where(c => c.Type == ClaimTypes.Role)
            .ToList();
        foreach (var staleRole in staleRoleClaims)
        {
            identity.TryRemoveClaim(staleRole);
        }

        foreach (var roleName in roleNames)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, roleName, ClaimValueTypes.String, "LOCAL AUTHORITY"));
        }

        return principal;
    }
}
