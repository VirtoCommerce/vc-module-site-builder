using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>
/// Writes of multilanguage pages, each checking the user and the request itself, in the order its endpoint always has.
/// Every write takes a dry run, and tells whether it reaches what the storefront serves.
/// </summary>
public interface IMultilanguagePageOperationService
{
    Task<SiteBuilderOperationResult<MultilanguagePage>> CreateAsync(MultilanguagePageSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Updates the root; the shared name and inherited settings reach the language versions.</summary>
    Task<SiteBuilderOperationResult<MultilanguagePage>> UpdateAsync(MultilanguagePageSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a draft version for a store language that has none.</summary>
    Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> CreateLanguageAsync(MultilanguagePageLanguageOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Makes an existing page a language version; the page and the root are checked in that order.</summary>
    Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> LinkAsync(MultilanguagePageLinkOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Turns a Landing Page into a Multilanguage Page with the page as its default language version.</summary>
    Task<SiteBuilderOperationResult<MultilanguagePage>> ConvertAsync(GroupedPageOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Marks an outdated translation as matching the current publication of its source language.</summary>
    Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> MarkUpToDateAsync(GroupedPageOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Turns a language version back into a Landing Page.</summary>
    Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> UnlinkAsync(GroupedPageOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Gives every language version the root values of the fields, dropping their overrides.</summary>
    /// <returns>Languages whose versions changed.</returns>
    Task<SiteBuilderOperationResult<IList<string>>> ResetOverridesAsync(MultilanguagePageResetOverridesOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Copies the multilanguage page with every language version as drafts.</summary>
    Task<SiteBuilderOperationResult<MultilanguagePage>> CloneAsync(MultilanguagePageOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Publishes the draft of every language version that has one.</summary>
    Task<SiteBuilderOperationResult<MultilanguagePageBatchResult>> PublishAllAsync(MultilanguagePageOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
