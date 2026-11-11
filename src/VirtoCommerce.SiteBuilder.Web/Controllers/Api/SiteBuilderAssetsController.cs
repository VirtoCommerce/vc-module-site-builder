using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.Platform.Core;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Authorization;

namespace VirtoCommerce.SiteBuilder.Web.Controllers.Api;

[Route("api/site-builder-assets")]
[Authorize]
public class SiteBuilderAssetsController(
    ISiteBuilderAssetReferenceService assetReferenceService,
    IAuthorizationService authorizationService)
    : Controller
{
    private const int MaximumPageSize = 100;

    [HttpPost("search")]
    [Authorize(PlatformConstants.Security.Permissions.AssetRead)]
    public async Task<ActionResult<SiteBuilderAssetSearchResult>> Search(
        [FromBody] SiteBuilderAssetSearchCriteria criteria,
        [FromServices] ISiteBuilderAssetSearchService assetSearchService,
        CancellationToken cancellationToken = default)
    {
        if (criteria == null || string.IsNullOrWhiteSpace(criteria.FolderUrl))
        {
            return BadRequest("FolderUrl is required.");
        }

        if (criteria.Skip < 0 || criteria.Take is < 1 or > MaximumPageSize)
        {
            return BadRequest($"Skip must be non-negative and Take must be between 1 and {MaximumPageSize}.");
        }

        if (criteria.SortInfos.Any(x => !SiteBuilderAssetSort.Columns.ContainsKey(x.SortColumn)))
        {
            return BadRequest($"Supported sort fields: {string.Join(", ", SiteBuilderAssetSort.Columns.Keys)}.");
        }

        return Ok(await assetSearchService.SearchAsync(criteria, cancellationToken));
    }

    [HttpPost("references")]
    [Authorize(ModuleConstants.Security.Permissions.Read)]
    public async Task<ActionResult<SiteBuilderAssetReferencesSearchResult>> SearchReferences(
        [FromBody] SiteBuilderAssetReferencesSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        if (criteria == null || string.IsNullOrWhiteSpace(criteria.StoreId))
        {
            return BadRequest("StoreId is required.");
        }

        var authorizationResult = await authorizationService.AuthorizeAsync(User, criteria, new SiteBuilderAuthorizationRequirement());
        if (!authorizationResult.Succeeded)
        {
            return Forbidden;
        }

        var result = await assetReferenceService.SearchReferencesAsync(criteria, cancellationToken);

        if (!await CanReadSharedComponentsAsync())
        {
            foreach (var reference in result.Results)
            {
                // Keep the aggregate reference count so delete preflight remains safe, but do not
                // expose Shared Component metadata through the broader Site Builder read permission.
                reference.SharedComponentReferencesCount = 0;
                reference.SharedComponents = [];
            }
        }

        return Ok(result);
    }

    private async Task<bool> CanReadSharedComponentsAsync()
    {
        var result = await authorizationService.AuthorizeAsync(
            User,
            null,
            ModuleConstants.Security.Permissions.SharedComponentsRead);
        return result.Succeeded;
    }

    private static ActionResult Forbidden => new ObjectResult(new { })
    {
        StatusCode = (int)HttpStatusCode.Forbidden,
    };
}
