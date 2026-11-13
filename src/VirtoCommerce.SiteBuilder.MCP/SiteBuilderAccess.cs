using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.SiteBuilder.Data.Authorization;
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
}
