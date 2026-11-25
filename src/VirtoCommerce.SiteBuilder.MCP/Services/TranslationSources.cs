using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

public class TranslationSources(IMultilanguagePageOperationService multilanguagePageOperations)
{
    public const string SourcePublishedAgain = "the source language version was published again after you read it";

    public virtual Task<(SiteBuilderChangePreview Preview, string Problem)> PreviewAsync(ClaimsPrincipal user, string pageId, DateTime? sourceRevisionDate,
        CancellationToken cancellationToken) =>
        MarkAsync(user, pageId, sourceRevisionDate, dryRun: true, cancellationToken);

    // Returns why it was not marked, or null.
    public virtual async Task<string> MarkUpToDateAsync(ClaimsPrincipal user, string pageId, DateTime? sourceRevisionDate, CancellationToken cancellationToken) =>
        (await MarkAsync(user, pageId, sourceRevisionDate, dryRun: false, cancellationToken)).Problem;

    private async Task<(SiteBuilderChangePreview Preview, string Problem)> MarkAsync(ClaimsPrincipal user, string pageId, DateTime? sourceRevisionDate, bool dryRun,
        CancellationToken cancellationToken)
    {
        // without it the operation marks the translation at whatever the source holds by then
        if (sourceRevisionDate == null)
        {
            return (null, "source_revision_date is missing: read the source language version and pass its source_revision_date");
        }

        var result = await multilanguagePageOperations.MarkUpToDateAsync(new MultilanguagePageMarkUpToDateOperationRequest
        {
            GroupId = pageId,
            ExpectedSourceRevisionDate = sourceRevisionDate,
            DryRun = dryRun,
        }, user, cancellationToken);

        return (result.Preview, result.Status switch
        {
            SiteBuilderOperationStatus.Succeeded => null,
            SiteBuilderOperationStatus.Conflict => SourcePublishedAgain,
            SiteBuilderOperationStatus.NotFound => "the page was deleted",
            SiteBuilderOperationStatus.Forbidden => "no builder:read in the store of the page",
            _ => string.IsNullOrEmpty(result.Error) ? $"not marked ({result.Status})" : result.Error.TrimEnd('.'),
        });
    }
}
