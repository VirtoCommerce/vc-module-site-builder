using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface IContentPermissionService
{
    /// <summary>The path has the theme folder.</summary>
    string GetThemeFileEditPermission(string path);

    string GetEditPermission(HistoryItem item);

    Task<bool> HasPermissionsAsync(ClaimsPrincipal user, IEnumerable<string> permissions);

    Task<bool> CanAccessPageAsync(ClaimsPrincipal user, GroupedSiteBuilderPage group, string storeId);
}
