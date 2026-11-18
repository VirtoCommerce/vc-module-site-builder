using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.MCP.Extensions;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// Shared Components of a store: sections kept once and placed on many pages. What a user sees follows the designer:
// the components with builder:shared-components:read in the store, the pages they stand on with builder:read there too.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class SharedComponentTools(
    IAuthorizationService authorizationService,
    ISiteBuilderSharedComponentOperationService sharedComponentOperationService,
    ISiteBuilderSharedComponentContentService sharedComponentContentService)
{
    // A component placed on every page of a large site would make the answer as long as the site; the usage service
    // reads every page anyway, so the rest is paged here with skip.
    public const int MaxUsagePages = 50;

    [McpServerTool(Name = "search_shared_components", ReadOnly = true, Destructive = false, Idempotent = true)]
    [McpPermission(Read, SharedComponentsRead)]
    [Description("Find the Shared Components of a store by name: sections kept once and placed on many pages, so a " +
        "change of one shows on every page it stands on. Returns one page of rows with the number of pages each stands " +
        "on. Pass shared_component_id to read one component instead: its content (settings and sections) and the " +
        "pages it stands on, 50 at most per call, with their total and usage_pages_next_skip for the next ones.")]
    public async Task<object> SearchSharedComponents(
        RequestContext<CallToolRequestParams> context,
        IMcpPageSizeResolver pageSizeResolver,
        [Description("Store id.")] string store_id,
        [Description("Text in the component name.")] string keyword = null,
        [Description("Id of one component to read with its content and pages. keyword is ignored.")] string shared_component_id = null,
        [Description("Rows to skip, from 0. Use next_skip of the previous page; with shared_component_id, the pages it stands on " +
            "to skip: use usage_pages_next_skip.")] int? skip = null,
        [Description("Rows to return. Omit it for the default page size. With shared_component_id, the pages it stands on to " +
            "return, at most 50.")] int? take = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(store_id))
        {
            throw new McpException("Pass store_id: Shared Components belong to one store.");
        }

        var criteria = new SiteBuilderSharedComponentSearchCriteria { StoreId = store_id, Keyword = keyword };
        await authorizationService.EnsureAllowedAsync(context.User, criteria, store_id, SharedComponentsRead);

        if (!string.IsNullOrEmpty(shared_component_id))
        {
            return await GetSharedComponentAsync(context, store_id, shared_component_id, new UsagePaging(skip, take), cancellationToken);
        }

        var paging = await pageSizeResolver.GetPagingAsync(skip, take);
        var result = await sharedComponentOperationService.SearchAsync(criteria.WithPaging(paging), context.User, cancellationToken);
        if (!result.Succeeded)
        {
            throw new McpForbiddenException($"Your {SharedComponentsRead} permission does not cover store '{store_id}'.");
        }

        return result.Value.ToMcpPage(paging, SharedComponentRow.From);
    }

    private async Task<SharedComponentDetails> GetSharedComponentAsync(RequestContext<CallToolRequestParams> context, string storeId,
        string sharedComponentId, UsagePaging paging, CancellationToken cancellationToken)
    {
        // A component of a store the user may not read is refused like a missing one, so the refusal names no store.
        var result = await sharedComponentOperationService.GetAsync(new SharedComponentOperationRequest { Id = sharedComponentId }, context.User, cancellationToken);
        var component = result.Succeeded ? result.Value : null;
        if (component == null || !storeId.EqualsIgnoreCase(component.StoreId))
        {
            throw new McpException($"Shared Component '{sharedComponentId}' was not found in store '{storeId}'. Call search_shared_components to find its id.");
        }

        // The operation lists the pages only with builder:read; this tells "not allowed" from "no pages".
        var canReadPages = await authorizationService.IsAllowedAsync(context.User, component);
        var content = SiteBuilderPageDocument.Read(await sharedComponentContentService.TryLoadContentAsync(component, cancellationToken));

        return SharedComponentDetails.From(component, canReadPages ? paging : null, content);
    }
}

public sealed class SharedComponentRow
{
    public string Id { get; init; }

    public string Name { get; init; }

    public string StoreId { get; init; }

    // Placements on pages, drafts and published versions together.
    public int UsageCount { get; init; }

    public DateTime? ModifiedDate { get; init; }

    public string ModifiedBy { get; init; }

    public static SharedComponentRow From(SiteBuilderSharedComponent component)
    {
        return new SharedComponentRow
        {
            Id = component.Id,
            Name = component.Name,
            StoreId = component.StoreId,
            UsageCount = component.UsageCount,
            ModifiedDate = component.ModifiedDate ?? component.CreatedDate,
            ModifiedBy = component.ModifiedBy ?? component.CreatedBy,
        };
    }
}

// The pages of one component the answer lists: from skip, at most MaxUsagePages.
public sealed record UsagePaging(int? Skip, int? Take)
{
    public int From => Math.Max(Skip ?? 0, 0);

    public int Count => Math.Clamp(Take ?? SharedComponentTools.MaxUsagePages, 1, SharedComponentTools.MaxUsagePages);
}

public sealed class SharedComponentDetails
{
    public string Id { get; init; }

    public string Name { get; init; }

    public string StoreId { get; init; }

    public int UsageCount { get; init; }

    // Null when the user may not read the store's pages; else one page of them.
    public IList<SiteBuilderSharedComponentUsagePage> UsagePages { get; init; }

    // Every page the component stands on, listed or not.
    public int? UsagePageCount { get; init; }

    // The skip of the next pages, or null when UsagePages holds the last ones.
    public int? UsagePagesNextSkip { get; init; }

    public string UsagePagesNote { get; init; }

    public DateTime CreatedDate { get; init; }

    public string CreatedBy { get; init; }

    public DateTime? ModifiedDate { get; init; }

    public string ModifiedBy { get; init; }

    public JsonObject Settings { get; init; }

    public JsonArray Sections { get; init; }

    // usagePaging is null when the user may not read the store's pages.
    public static SharedComponentDetails From(SiteBuilderSharedComponent component, UsagePaging usagePaging, SiteBuilderPageDocument content)
    {
        var pages = component.UsagePages ?? [];
        var listed = usagePaging == null ? null : pages.Skip(usagePaging.From).Take(usagePaging.Count).ToList();
        var nextSkip = listed != null && usagePaging.From + listed.Count < pages.Count ? usagePaging.From + listed.Count : (int?)null;

        return new SharedComponentDetails
        {
            Id = component.Id,
            Name = component.Name,
            StoreId = component.StoreId,
            UsageCount = component.UsageCount,
            UsagePages = listed,
            UsagePageCount = listed == null ? null : pages.Count,
            UsagePagesNextSkip = nextSkip,
            UsagePagesNote = DescribeUsagePages(listed, usagePaging, pages.Count, nextSkip),
            CreatedDate = component.CreatedDate,
            CreatedBy = component.CreatedBy,
            ModifiedDate = component.ModifiedDate,
            ModifiedBy = component.ModifiedBy,
            Settings = content?.Settings,
            Sections = content?.Sections,
        };
    }

    private static string DescribeUsagePages(IList<SiteBuilderSharedComponentUsagePage> listed, UsagePaging paging, int total, int? nextSkip)
    {
        if (listed == null)
        {
            return "The pages are not listed: your builder:read permission does not cover this store.";
        }

        return nextSkip == null
            ? null
            : $"Pages {paging.From + 1}-{nextSkip} of {total} are listed. Call again with shared_component_id and skip {nextSkip} for the next ones.";
    }
}
