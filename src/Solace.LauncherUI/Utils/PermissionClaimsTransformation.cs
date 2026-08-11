using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace Solace.LauncherUI.Utils;

/// <summary>
/// Refreshes "Permission" claims from the database on every request.
/// This ensures newly auto-seeded permissions take effect immediately
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
        if (roleNames.Count == 0)
        {
            return principal;
        }

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

        if (freshPermissions.Count == 0)
        {
            return principal;
        }

        // Replace existing Permission claims with fresh ones
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

        return principal;
    }
}
