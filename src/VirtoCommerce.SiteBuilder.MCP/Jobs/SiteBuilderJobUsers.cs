using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.Platform.Core.Security;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Jobs;

// The user a job of a bulk tool runs as: the one who called the tool, as a sign-in would give them now, with the
// roles and permissions they have when the job runs rather than when it was queued.
public class SiteBuilderJobUsers(
    Func<UserManager<ApplicationUser>> userManagerFactory,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsPrincipalFactory,
    IUserNameResolver userNameResolver)
{
    // Null when the account no longer exists or is locked out.
    public virtual async Task<ClaimsPrincipal> CreateAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        using var userManager = userManagerFactory();
        var account = await userManager.FindByIdAsync(userId);
        if (account == null || (userManager.SupportsUserLockout && await userManager.IsLockedOutAsync(account)))
        {
            return null;
        }

        return await claimsPrincipalFactory.CreateAsync(account);
    }

    // Why the user may not change any page now, or null. The tools require builder:update granted in every store, as
    // the designer's save does; the store of each page is checked page by page.
    public static string GetRefusal(ClaimsPrincipal user)
    {
        if (user == null)
        {
            return "the account that started the job no longer exists or is locked";
        }

        return user.GetMcpPermissionGrant(Update)?.IsGlobal == true ? null : "the account no longer has builder:update";
    }

    // Entities saved by the job name the user as their author; without it a job has no user name to record.
    public virtual void SetCurrentUser(ClaimsPrincipal user)
    {
        userNameResolver.SetCurrentUserName(user.Identity?.Name);
    }
}
