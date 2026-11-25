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

namespace VirtoCommerce.SiteBuilder.MCP.Extensions;

// The tool-level permission check passes with the permission in any store; these narrow it to the store at hand.
public static class AuthorizationServiceExtensions
{
    public static async Task<bool> IsAllowedAsync(this IAuthorizationService authorizationService, ClaimsPrincipal user,
        object resource, string permission = Read)
    {
        var result = await authorizationService.AuthorizeAsync(user, resource, new SiteBuilderAuthorizationRequirement(permission));

        return result.Succeeded;
    }

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

    // Callers refuse a missing store first: without one, a permission granted in every store passes.
    public static Task<bool> IsStoreAllowedAsync(this IAuthorizationService authorizationService, ClaimsPrincipal user, string storeId,
        string permission = Read) =>
        authorizationService.IsAllowedAsync(user, new SiteBuilderPageSearchCriteria { StoreId = storeId }, permission);

    public static Task EnsureStoreAllowedAsync(this IAuthorizationService authorizationService, ClaimsPrincipal user, string storeId,
        string permission = Read) =>
        authorizationService.EnsureAllowedAsync(user, new SiteBuilderPageSearchCriteria { StoreId = storeId }, storeId, permission);

    // Null means every store.
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
            if (await authorizationService.IsStoreAllowedAsync(user, storeId, permission))
            {
                storeIds.Add(storeId);
            }
        }

        return storeIds;
    }

    public static async Task<IList<string>> GetStoreIdsAsync(this IAuthorizationService authorizationService, ClaimsPrincipal user,
        Func<UserManager<ApplicationUser>> userManagerFactory, string storeId, string permission)
    {
        if (string.IsNullOrEmpty(storeId))
        {
            return await authorizationService.GetStoreIdsAsync(user, userManagerFactory, permission);
        }

        return await authorizationService.IsStoreAllowedAsync(user, storeId, permission) ? [storeId] : [];
    }

    private static async Task<string> GetBoundStoreIdAsync(ClaimsPrincipal user, Func<UserManager<ApplicationUser>> userManagerFactory)
    {
        using var userManager = userManagerFactory();
        var account = await userManager.FindByIdAsync(user.GetUserId());

        return account?.StoreId;
    }
}
