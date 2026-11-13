using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.StoreModule.Core.Security;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP;

// [McpPermission] only says that the user has the permission in some store. The tools narrow it to the stores the
// permission covers with the requirement the controllers use, so an account bound to a store and a role limited to
// some stores reach the same data here as in the designer.
public static class SiteBuilderAccess
{
    public static async Task<bool> IsAllowedAsync(this IAuthorizationService authorizationService, ClaimsPrincipal user,
        object resource, string permission = Read)
    {
        var result = await authorizationService.AuthorizeAsync(user, resource, new SiteBuilderAuthorizationRequirement(permission));

        return result.Succeeded;
    }

    // The store of the resource (a criteria with StoreId, a page, a component); without a store only a permission
    // granted in every store passes.
    public static async Task EnsureAllowedAsync(this IAuthorizationService authorizationService, ClaimsPrincipal user,
        object resource, string storeId, string permission = Read)
    {
        if (await authorizationService.IsAllowedAsync(user, resource, permission))
        {
            return;
        }

        throw new McpForbiddenException(string.IsNullOrEmpty(storeId)
            ? $"Your {permission} permission does not cover every store. Pass store_id of a store you work in."
            : $"Your {permission} permission does not cover store '{storeId}'.");
    }

    // The stores the permission reaches when the agent names none: null for every store. A role limited to some
    // stores reaches those, an account bound to a store reaches that store; each is checked with the requirement, so
    // the answer is what the designer would let the user open one by one.
    public static async Task<IList<string>> GetStoreIdsAsync(this IAuthorizationService authorizationService, ClaimsPrincipal user,
        Func<UserManager<ApplicationUser>> userManagerFactory, string permission = Read)
    {
        if (await authorizationService.IsAllowedAsync(user, new SiteBuilderPageSearchCriteria(), permission))
        {
            return null;
        }

        var grant = user.GetMcpPermissionGrant(permission);
        if (grant == null)
        {
            return [];
        }

        // A permission granted in every store misses here only for an account bound to a store.
        IList<string> candidates = grant.IsGlobal
            ? [await GetBoundStoreIdAsync(user, userManagerFactory)]
            : grant.GetScopeValues(nameof(StoreSelectedScope));

        var storeIds = new List<string>();
        foreach (var storeId in candidates.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await authorizationService.IsAllowedAsync(user, new SiteBuilderPageSearchCriteria { StoreId = storeId }, permission))
            {
                storeIds.Add(storeId);
            }
        }

        return storeIds;
    }

    // The stores among the named one, or among every store when none is named, where the permission holds: null for
    // every store. Unlike GetStoreIdsAsync it does not refuse; a scan leaves out what the user may not open.
    public static async Task<IList<string>> GetStoreIdsAsync(this IAuthorizationService authorizationService, ClaimsPrincipal user,
        Func<UserManager<ApplicationUser>> userManagerFactory, string storeId, string permission)
    {
        if (string.IsNullOrEmpty(storeId))
        {
            return await authorizationService.GetStoreIdsAsync(user, userManagerFactory, permission);
        }

        return await authorizationService.IsAllowedAsync(user, new SiteBuilderPageSearchCriteria { StoreId = storeId }, permission) ? [storeId] : [];
    }

    private static async Task<string> GetBoundStoreIdAsync(ClaimsPrincipal user, Func<UserManager<ApplicationUser>> userManagerFactory)
    {
        using var userManager = userManagerFactory();
        var account = await userManager.FindByIdAsync(user.GetUserId());

        return account?.StoreId;
    }
}
