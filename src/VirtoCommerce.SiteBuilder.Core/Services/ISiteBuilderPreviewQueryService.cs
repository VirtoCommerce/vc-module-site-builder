using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>Where the store's storefront shows its pages and templates, checking the user and the request itself.</summary>
public interface ISiteBuilderPreviewQueryService
{
    /// <summary>The storefront's address, and the path of its page that previews what the designer sends it.</summary>
    Task<SiteBuilderOperationResult<DesignerStorefront>> GetStorefrontAsync(SiteBuilderOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The storefront's preview of a page kept in git at a version: the one asked for, else the user's draft, else what is published.
    /// Not found for a store that keeps no pages in git, and, telling the page in its value, for a page with no version to show.
    /// Invalid for a store without a storefront address.
    /// </summary>
    Task<SiteBuilderOperationResult<SiteBuilderPreviewLink>> GetFilePagePreviewAsync(SiteBuilderFilePagePreviewRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>The storefront's preview of a page of the database, as its draft stands.</summary>
    Task<SiteBuilderOperationResult<SiteBuilderPreviewLink>> GetGroupedPagePreviewAsync(GroupedPageQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The storefront page a template is shown on: the sample page the template schema of its type names. It shows the published theme;
    /// a draft of the template is seen in the designer. Not found when the schema names no sample page.
    /// </summary>
    Task<SiteBuilderOperationResult<SiteBuilderPreviewLink>> GetTemplatePreviewAsync(SiteBuilderTemplatePreviewRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
