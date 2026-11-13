using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;

namespace VirtoCommerce.SiteBuilder.MCP;

// The page or multilanguage page a write names, found and checked against the stores of the user before the
// operation runs, so a refusal names the store. The operation checks the store again, as its endpoint does.
public static class PageLookup
{
    // A page kept as a file is named by its path in the store, as the designer opens it; the id of a database page never
    // holds a '/'.
    public static bool IsFilePagePath(string pageId) => pageId?.Contains('/') == true;

    public static async Task<GroupedSiteBuilderPage> LoadPageAsync(this IGroupedPageService groupedPageService, IAuthorizationService authorizationService,
        ClaimsPrincipal user, string pageId, string argumentName = "page_id")
    {
        if (string.IsNullOrWhiteSpace(pageId))
        {
            throw new McpException($"Pass {argumentName}; search_site_pages finds it.");
        }

        if (IsFilePagePath(pageId))
        {
            throw new McpException($"'{pageId}' is a page stored as a file. This tool is not available for pages stored as files yet; " +
                "use the designer for it.");
        }

        var group = await groupedPageService.GetByIdAsync(pageId)
            ?? throw new McpException($"Page '{pageId}' was not found. Call search_site_pages to find its id.");

        await authorizationService.EnsureAllowedAsync(user, group, group.StoreId);

        return group;
    }

    public static async Task<MultilanguagePage> LoadMultilanguagePageAsync(this IMultilanguagePageService multilanguagePageService,
        IAuthorizationService authorizationService, ClaimsPrincipal user, string multilanguagePageId)
    {
        if (string.IsNullOrWhiteSpace(multilanguagePageId))
        {
            throw new McpException("Pass multilanguage_page_id: the id of a Multilanguage Page row of search_site_pages.");
        }

        var root = await multilanguagePageService.GetByIdAsync(multilanguagePageId)
            ?? throw new McpException($"Multilanguage Page '{multilanguagePageId}' was not found. Call search_site_pages to find its id.");

        await authorizationService.EnsureAllowedAsync(user, root, root.StoreId);

        return root;
    }

    public static string ForbiddenPage(GroupedSiteBuilderPage group) =>
        $"You may not change page '{group.Id}': it needs builder:read in store '{group.StoreId}', and builder:shared-components:read " +
        "there when the page places Shared Components.";

    public static string ForbiddenMultilanguagePage(MultilanguagePage root) =>
        $"You may not change Multilanguage Page '{root.Id}': it needs builder:read in store '{root.StoreId}'.";

    // A time without a kind is read as UTC, as the stored dates are.
    public static DateTime? ToUtc(DateTime? value) => value?.Kind switch
    {
        null => null,
        DateTimeKind.Local => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
    };
}
