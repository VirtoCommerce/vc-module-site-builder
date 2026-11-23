using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
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
using VirtoCommerce.SiteBuilder.MCP.Extensions;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class AssetTools(
    IAuthorizationService authorizationService,
    ISiteBuilderAssetSearchService assetSearchService,
    ISiteBuilderAssetReferenceService assetReferenceService)
{
    private const int MaxPagesPerAsset = 10;

    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
        Justification = "Each parameter is an argument of the MCP tool's input schema.")]
    [McpServerTool(Name = "search_assets", ReadOnly = true, Destructive = false, Idempotent = true)]
    [McpPermission(Read)]
    [Authorize(PlatformConstants.Security.Permissions.AssetRead)]
    [Description("List the folders and files of one folder of the asset library, folders first, with type, size and " +
        "change date. With store_id, each file also says how many pages and Shared Components of that store use it " +
        "and names the pages (Shared Components only with builder:shared-components:read in the store). The library " +
        "of a store opens at '/stores/{store_id}/Page Builder'; open a subfolder with the url of its row. The url of a file " +
        "is its public link, the one to put in a page, a template or the theme.")]
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
            await authorizationService.EnsureStoreAllowedAsync(context.User, store_id);
            canReadSharedComponents = await authorizationService.IsStoreAllowedAsync(context.User, store_id, SharedComponentsRead);
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

        // The service clamps a skip past the end, so report the skip it used.
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

    public string Url { get; init; }

    public string Type { get; init; }

    public string ContentType { get; init; }

    public long? Size { get; init; }

    public DateTime? ModifiedDate { get; init; }

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
            // a page or the theme holds a file's public link, as the designer saves it; a folder is opened by its library path
            Url = entry.Type == FolderType || string.IsNullOrEmpty(entry.Url) ? url : entry.Url,
            Type = entry.Type,
            ContentType = blob?.ContentType,
            Size = blob?.Size,
            ModifiedDate = entry.ModifiedDate,
            References = GetReferences(references, normalizedUrl),
        };
    }

    private static AssetReferencesRow GetReferences(IDictionary<string, AssetReferencesRow> references, string normalizedUrl)
    {
        if (references == null || string.IsNullOrEmpty(normalizedUrl))
        {
            return null;
        }

        return references.TryGetValue(normalizedUrl, out var row) ? row : new AssetReferencesRow { Pages = [] };
    }
}

public sealed class AssetReferencesRow
{
    public int Count { get; init; }

    public int PageCount { get; init; }

    public int? SharedComponentCount { get; init; }

    public IList<SiteBuilderAssetReferencePage> Pages { get; init; }

    public IList<SiteBuilderAssetReferenceSharedComponent> SharedComponents { get; init; }

    public static AssetReferencesRow From(SiteBuilderAssetReference reference, bool canReadSharedComponents, int maxPages)
    {
        // The total counts Shared Components even without their permission, so a file in use never reads as unused.
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
