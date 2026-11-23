using System.Security.Claims;
using VirtoCommerce.ContentModule.Core.Model;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>Whether a page is published and has changes that are not, checking the user and the request itself.</summary>
public interface ISiteBuilderPublishStatusQueryService
{
    /// <summary>
    /// A page or a blog article kept as a file, in git or in blob storage, both in the same shape. In git it tells too whether a pull
    /// request is open, where the page stands on the production branch, and whether the live copy lags behind.
    /// Without a path it tells the flow only.
    /// </summary>
    Task<SiteBuilderOperationResult<PagePublishStatus>> GetFilePageStatusAsync(SiteBuilderFilePageQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>A page of the database. The page is looked up before access to it is checked.</summary>
    Task<SiteBuilderOperationResult<FilePublishStatus>> GetGroupedPageStatusAsync(GroupedPageQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
