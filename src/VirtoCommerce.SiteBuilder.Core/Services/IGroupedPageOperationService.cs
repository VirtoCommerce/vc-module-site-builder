using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>
/// Writes of a page group, each checking the user and the request itself, in the order its endpoint always has.
/// Every write takes a dry run, and tells whether it reaches what the storefront serves.
/// </summary>
public interface IGroupedPageOperationService
{
    /// <summary>Creates the group with an empty draft. The language version fields are not taken: only a multilanguage page links a page.</summary>
    Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> CreateAsync(GroupedPageSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Creates the group with a draft holding the content, its HTML sanitized.</summary>
    Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> CreateWithContentAsync(GroupedPageCreateOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the settings of the group, or creates it when it does not exist. A move to another store needs access to both stores.
    /// </summary>
    Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> UpdateAsync(GroupedPageSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Publishes the draft, or turns the published page without changes back into a draft.</summary>
    Task<SiteBuilderOperationResult> PublishAsync(GroupedPagePublishOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Drops the draft, so the page is what is published again.</summary>
    Task<SiteBuilderOperationResult> DiscardDraftAsync(GroupedPageDiscardOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the designer document (<c>{ "settings": {}, "content": [] }</c>) into the draft, with its HTML sanitized.
    /// The document is checked before the page is looked up.
    /// </summary>
    Task<SiteBuilderOperationResult<UpdatePageContentResult>> SaveContentAsync(GroupedPageContentSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves any well-formed JSON into the draft, with its HTML sanitized: content stored by older designer versions can be an array.
    /// The content is read and checked after the access to the page.
    /// </summary>
    Task<SiteBuilderOperationResult<UpdatePageContentResult>> SaveRawContentAsync(GroupedPageContentSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>Copies the content of the source page last changed, draft or published, into the draft of the page.</summary>
    Task<SiteBuilderOperationResult> CopyContentAsync(GroupedPageContentCopyOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
