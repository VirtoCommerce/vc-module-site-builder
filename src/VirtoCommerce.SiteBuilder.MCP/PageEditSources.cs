using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP;

// The pages and Shared Components the section operations read, as the user may read them: a page to copy sections
// from in any store of the user, a Shared Component only of the store of the page being written.
public sealed class PageEditSources(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService pageContentService,
    ISiteBuilderSharedComponentService sharedComponentService,
    ISiteBuilderSharedComponentContentService sharedComponentContentService,
    ClaimsPrincipal user,
    string storeId) : IPageEditSources
{
    public async Task<JsonArray> GetPageSectionsAsync(string pageId, CancellationToken cancellationToken)
    {
        var group = await groupedPageService.GetByIdAsync(pageId)
            ?? throw new McpException($"Page '{pageId}' to copy from was not found. Call search_site_pages to find its id.");

        await authorizationService.EnsureAllowedAsync(user, group, group.StoreId);

        var snapshot = await LoadDraftAsync(pageContentService, group, cancellationToken);
        return SiteBuilderPageDocument.Read(snapshot.Content)?.Sections ?? [];
    }

    public async Task EnsureSharedComponentAsync(string sharedComponentId, CancellationToken cancellationToken)
    {
        await GetSharedComponentAsync(sharedComponentId);
    }

    public async Task<JsonArray> GetSharedComponentSectionsAsync(string sharedComponentId, CancellationToken cancellationToken)
    {
        var component = await GetSharedComponentAsync(sharedComponentId);
        var content = SiteBuilderPageDocument.Read(await sharedComponentContentService.TryLoadContentAsync(component, cancellationToken));

        return content?.Sections ?? throw new McpException($"Shared Component '{sharedComponentId}' has no content to detach.");
    }

    // The draft, or the published version when the page has no draft.
    public static async Task<SiteBuilderContentSnapshot> LoadDraftAsync(IGroupedPageContentService pageContentService, GroupedSiteBuilderPage group,
        CancellationToken cancellationToken)
    {
        try
        {
            return await pageContentService.LoadGroupContentAsync(group, draft: true, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            throw new McpException($"Page '{group.Id}' was deleted while it was read.");
        }
    }

    private async Task<SiteBuilderSharedComponent> GetSharedComponentAsync(string sharedComponentId)
    {
        var component = await sharedComponentService.GetByIdAsync(sharedComponentId);
        if (component == null || !storeId.EqualsIgnoreCase(component.StoreId))
        {
            throw new McpException($"Shared Component '{sharedComponentId}' was not found in store '{storeId}'. Call search_shared_components to find its id.");
        }

        await authorizationService.EnsureAllowedAsync(user, component, component.StoreId, SharedComponentsRead);
        return component;
    }
}
