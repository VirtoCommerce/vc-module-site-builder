using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>
/// Shared components, checking the user and the request itself, in the order their endpoints always have. A component is answered with how
/// many pages use it, and which pages only to a user who may read the pages of its store. Every write takes a dry run.
/// </summary>
public interface ISiteBuilderSharedComponentOperationService
{
    Task<SiteBuilderOperationResult<SiteBuilderSharedComponentSearchResult>> SearchAsync(SiteBuilderSharedComponentSearchCriteria criteria,
        ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>The component is looked up before access to it is checked.</summary>
    Task<SiteBuilderOperationResult<SiteBuilderSharedComponent>> GetAsync(SharedComponentOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Creates the component with its content, its HTML sanitized. The request is checked before access to its store.</summary>
    Task<SiteBuilderOperationResult<SiteBuilderSharedComponent>> CreateAsync(SharedComponentCreateOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Renames the component; it stays in its store. Access to it is checked before the request.</summary>
    Task<SiteBuilderOperationResult<SiteBuilderSharedComponent>> UpdateAsync(SharedComponentUpdateOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the content of the component, its HTML sanitized: every page it is placed on shows it, published ones as soon as it is saved.
    /// A dry run answers the content and the pages it is placed on. Access to the component is checked before the request.
    /// </summary>
    Task<SiteBuilderOperationResult> SaveContentAsync(SharedComponentContentSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
