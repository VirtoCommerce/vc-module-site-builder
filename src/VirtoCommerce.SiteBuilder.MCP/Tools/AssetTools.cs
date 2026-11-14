using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.AssetsModule.Core.Assets;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.Platform.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// Images and files of the asset library, and where a store's pages use them.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class AssetTools(
    IAuthorizationService authorizationService,
    ISiteBuilderAssetSearchService assetSearchService,
    ISiteBuilderAssetReferenceService assetReferenceService)
{
    // Pages named per asset; the count covers them all.
    private const int MaxPagesPerAsset = 10;

    [McpServerTool(Name = "search_assets", ReadOnly = true, Destructive = false, Idempotent = true)]
    [McpPermission(Read)]
    [Authorize(PlatformConstants.Security.Permissions.AssetRead)]
    [Description("List the folders and files of one folder of the asset library, folders first, with type, size and " +
        "change date. With store_id, each file also says how many pages and Shared Components of that store use it " +
        "and names the pages (Shared Components only with builder:shared-components:read in the store). The library " +
        "of a store opens at '/stores/{store_id}/Page Builder'; open a subfolder with the url of its row.")]
    public async Task<McpPage<AssetRow>> SearchAssets(
        RequestContext<CallToolRequestParams> context,
        IMcpPageSizeResolver pageSizeResolver,
        [Description("The folder to list, for example '/stores/B2B-store/Page Builder'.")] string folder_url,
        [Description("Text in the file or folder name.")] string keyword = null,
        [Description("Only files of this kind: a MIME type such as 'image/*' or 'application/pdf', or an extension such as '.svg'. Folders are always listed.")] string type = null,
        [Description("Store whose pages and Shared Components are checked for the files. Omit it to list the files without their use.")] string store_id = null,
        [Description("Rows to skip, from 0. Use next_skip of the previous page.")] int? skip = null,
        [Description("Rows to return. Omit it for the default page size.")] int? take = null,
        CancellationToken cancellationToken = default)
    {
        var canReadSharedComponents = false;
        if (!string.IsNullOrEmpty(store_id))
        {
            var store = new SiteBuilderAssetReferencesSearchCriteria { StoreId = store_id };
            await authorizationService.EnsureAllowedAsync(context.User, store, store_id);
            canReadSharedComponents = await authorizationService.IsAllowedAsync(context.User, store, SharedComponentsRead);
        }

        if (string.IsNullOrWhiteSpace(folder_url))
        {
            throw new McpException("Pass folder_url, for example '/stores/{store_id}/Page Builder'.");
        }

        var paging = await pageSizeResolver.GetPagingAsync(skip, take);
        var criteria = new SiteBuilderAssetSearchCriteria
        {
            FolderUrl = folder_url,
            Keyword = keyword,
            AcceptedTypes = string.IsNullOrWhiteSpace(type) ? [] : [type],
        }.WithPaging(paging);

        var result = await assetSearchService.SearchAsync(criteria, cancellationToken);
        var references = string.IsNullOrEmpty(store_id)
            ? null
            : await GetReferencesAsync(store_id, result.Results, canReadSharedComponents, cancellationToken);

        // The service turns a skip past the end into the last page; the page says which rows it holds.
        return McpPage<AssetRow>.Create(
            result.Results.Select(x => AssetRow.From(x, references)),
            result.TotalCount,
            new McpPaging(result.Skip, paging.Take));
    }

    private async Task<IDictionary<string, AssetReferencesRow>> GetReferencesAsync(string storeId, IEnumerable<BlobEntry> entries,
        bool canReadSharedComponents, CancellationToken cancellationToken)
    {
        var assetUrls = entries.Where(x => x.Type != AssetRow.FolderType).Select(AssetRow.GetUrl).Where(x => !string.IsNullOrEmpty(x)).ToList();
        if (assetUrls.Count == 0)
        {
            return new Dictionary<string, AssetReferencesRow>();
        }

        var result = await assetReferenceService.SearchReferencesAsync(new SiteBuilderAssetReferencesSearchCriteria
        {
            StoreId = storeId,
            AssetUrls = assetUrls,
            IncludePages = true,
        }, cancellationToken);

        return result.Results
            .Where(x => !string.IsNullOrEmpty(x.NormalizedAssetUrl))
            .ToDictionary(
                x => x.NormalizedAssetUrl,
                x => AssetReferencesRow.From(x, canReadSharedComponents, MaxPagesPerAsset),
                StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class AssetRow
{
    public const string FolderType = "folder";

    public string Name { get; init; }

    // Pass it as folder_url to open a folder.
    public string Url { get; init; }

    // folder or blob.
    public string Type { get; init; }

    public string ContentType { get; init; }

    public long? Size { get; init; }

    public DateTime? ModifiedDate { get; init; }

    // With store_id, for files: where the store uses the file.
    public AssetReferencesRow References { get; init; }

    public static string GetUrl(BlobEntry entry) => string.IsNullOrEmpty(entry.RelativeUrl) ? entry.Url : entry.RelativeUrl;

    public static AssetRow From(BlobEntry entry, IDictionary<string, AssetReferencesRow> references)
    {
        var url = GetUrl(entry);
        var blob = entry as BlobInfo;
        var normalizedUrl = entry.Type == FolderType ? null : SiteBuilderAssetReferenceMatcher.NormalizeAssetUrl(url);

        return new AssetRow
        {
            Name = entry.Name,
            Url = url,
            Type = entry.Type,
            ContentType = blob?.ContentType,
            Size = blob?.Size,
            ModifiedDate = entry.ModifiedDate,
            References = references == null || string.IsNullOrEmpty(normalizedUrl)
                ? null
                : references.TryGetValue(normalizedUrl, out var row) ? row : new AssetReferencesRow { Pages = [] },
        };
    }
}

public sealed class AssetReferencesRow
{
    // Pages and Shared Components together, including those not listed below.
    public int Count { get; init; }

    public int PageCount { get; init; }

    // Null when the user may not read the store's Shared Components.
    public int? SharedComponentCount { get; init; }

    public IList<SiteBuilderAssetReferencePage> Pages { get; init; }

    public IList<SiteBuilderAssetReferenceSharedComponent> SharedComponents { get; init; }

    public static AssetReferencesRow From(SiteBuilderAssetReference reference, bool canReadSharedComponents, int maxPages)
    {
        // The total stays, as in the designer, so a file in use never reads as unused; the components themselves
        // need their own permission.
        return new AssetReferencesRow
        {
            Count = reference.ReferencesCount,
            PageCount = reference.PageReferencesCount,
            SharedComponentCount = canReadSharedComponents ? reference.SharedComponentReferencesCount : null,
            Pages = reference.Pages.Take(maxPages).ToList(),
            SharedComponents = canReadSharedComponents ? reference.SharedComponents : null,
        };
    }
}
