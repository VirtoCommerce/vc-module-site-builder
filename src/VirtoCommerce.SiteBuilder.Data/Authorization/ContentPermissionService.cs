using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Data.Authorization;

public class ContentPermissionService(IAuthorizationService authorizationService) : IContentPermissionService
{
    public virtual string GetThemeFileEditPermission(string path) =>
        IsTemplateFile(path) ? ModuleConstants.Security.Permissions.Templates : ModuleConstants.Security.Permissions.Theme;

    public virtual string GetEditPermission(HistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item.Type == HistoryItemType.Page ? ModuleConstants.Security.Permissions.Update : GetThemeFileEditPermission(item.Key);
    }

    public virtual Task<bool> HasPermissionsAsync(ClaimsPrincipal user, IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        return HasPermissionsInternalAsync(user, permissions);
    }

    private async Task<bool> HasPermissionsInternalAsync(ClaimsPrincipal user, IEnumerable<string> permissions)
    {
        foreach (var permission in permissions.Distinct())
        {
            if (!(await authorizationService.AuthorizeAsync(user, null, permission)).Succeeded)
            {
                return false;
            }
        }

        return true;
    }

    public virtual Task<bool> CanAccessPageAsync(ClaimsPrincipal user, GroupedSiteBuilderPage group, string storeId)
    {
        ArgumentNullException.ThrowIfNull(group);

        return CanAccessPageInternalAsync(user, group, storeId);
    }

    private async Task<bool> CanAccessPageInternalAsync(ClaimsPrincipal user, GroupedSiteBuilderPage group, string storeId)
    {
        var authorizationResult = await authorizationService.AuthorizeAsync(user, group, new SiteBuilderAuthorizationRequirement());

        return authorizationResult.Succeeded && group.StoreId.EqualsIgnoreCase(storeId);
    }

    protected virtual bool IsTemplateFile(string path)
    {
        var pathInTheme = SiteBuilderContentPaths.GetPathInTheme(path);

        return SiteBuilderContentPaths.IsInFolder(pathInTheme, ThemeStorage.TemplatesFolder) ||
               pathInTheme.Equals(ThemeStorage.TemplateRulesFile, StringComparison.OrdinalIgnoreCase);
    }
}
