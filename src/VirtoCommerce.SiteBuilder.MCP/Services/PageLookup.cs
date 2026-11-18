using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.MCP.Extensions;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

// The page or multilanguage page a write names, found and checked against the stores of the user before the
// operation runs. The operation checks the store again, as its endpoint does.
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
            throw new McpException($"'{pageId}' is a page stored as a file, and this tool works on database pages only. A page stored " +
                "as a file is read with search_site_pages and changed with edit_page_draft, publish_page, unpublish_page, discard_draft " +
                "and restore_version; anything else, in the designer.");
        }

        var group = await groupedPageService.GetByIdAsync(pageId)
            ?? throw new McpException($"Page '{pageId}' was not found. Call search_site_pages to find its id.");

        // The caller did not name the page's store and may not know it, so the refusal leaves it out.
        if (!await authorizationService.IsAllowedAsync(user, group))
        {
            throw new McpForbiddenException($"Your {Read} permission does not cover the store of page '{pageId}'.");
        }

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

        if (!await authorizationService.IsAllowedAsync(user, root))
        {
            throw new McpForbiddenException($"Your {Read} permission does not cover the store of Multilanguage Page '{multilanguagePageId}'.");
        }

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
