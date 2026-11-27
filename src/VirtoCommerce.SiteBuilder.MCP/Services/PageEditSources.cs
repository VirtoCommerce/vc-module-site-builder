using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using Newtonsoft.Json;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.MCP.Extensions;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "Each service is one the edit reads from, and the user and the store scope those reads; a parameter object would only hide them.")]
public sealed class PageEditSources(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService pageContentService,
    ISiteBuilderSharedComponentService sharedComponentService,
    ISiteBuilderSharedComponentContentService sharedComponentContentService,
    ISchemaCatalogService schemaCatalogService,
    ICustomSectionService customSectionService,
    ClaimsPrincipal user,
    string storeId) : IPageEditSources
{
    private readonly Dictionary<string, string> _pageVersions = new(StringComparer.OrdinalIgnoreCase);
    private Task<JsonObject> _schemas;

    public IReadOnlyDictionary<string, string> PageVersions => _pageVersions;

    public async Task<JsonArray> GetPageSectionsAsync(string pageId, CancellationToken cancellationToken)
    {
        var group = await groupedPageService.GetByIdAsync(pageId)
            ?? throw new McpException($"Page '{pageId}' to copy from was not found. Call search_site_pages to find its id.");

        await authorizationService.EnsureAllowedAsync(user, group, group.StoreId);

        var snapshot = await LoadDraftAsync(pageContentService, group, cancellationToken);
        _pageVersions[group.Id] = snapshot.ETag;
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

    public Task<JsonObject> GetSchemasAsync(CancellationToken cancellationToken) => _schemas ??= ReadSchemasAsync(cancellationToken);

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

    private async Task<JsonObject> ReadSchemasAsync(CancellationToken cancellationToken)
    {
        var catalog = await ThemeFiles.GetCatalogWithCustomSectionsAsync(schemaCatalogService, customSectionService, storeId, cancellationToken);

        return catalog == null ? null : (JsonObject)JsonNode.Parse(catalog.ToString(Formatting.None));
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
