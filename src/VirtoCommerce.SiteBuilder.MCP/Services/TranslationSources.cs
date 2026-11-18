using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

// A translation is marked only while the source is still at the revision the agent read, through the designer's own mark up to date.
public class TranslationSources(IMultilanguagePageOperationService multilanguagePageOperations)
{
    public const string SourcePublishedAgain = "the source language version was published again after you read it";

    // Why the translation cannot be marked, or null.
    public virtual Task<string> GetProblemAsync(ClaimsPrincipal user, string pageId, DateTime? sourceRevisionDate, CancellationToken cancellationToken) =>
        MarkAsync(user, pageId, sourceRevisionDate, dryRun: true, cancellationToken);

    // Marks the translation at the source revision the agent read, as the user; why it was not marked, or null.
    public virtual Task<string> MarkUpToDateAsync(ClaimsPrincipal user, string pageId, DateTime? sourceRevisionDate, CancellationToken cancellationToken) =>
        MarkAsync(user, pageId, sourceRevisionDate, dryRun: false, cancellationToken);

    private async Task<string> MarkAsync(ClaimsPrincipal user, string pageId, DateTime? sourceRevisionDate, bool dryRun, CancellationToken cancellationToken)
    {
        // without it the operation marks the translation at whatever the source holds by then
        if (sourceRevisionDate == null)
        {
            return "source_revision_date is missing: read the source language version and pass its source_revision_date";
        }

        var result = await multilanguagePageOperations.MarkUpToDateAsync(new MultilanguagePageMarkUpToDateOperationRequest
        {
            GroupId = pageId,
            ExpectedSourceRevisionDate = sourceRevisionDate,
            DryRun = dryRun,
        }, user, cancellationToken);

        return result.Status switch
        {
            SiteBuilderOperationStatus.Succeeded => null,
            SiteBuilderOperationStatus.Conflict => SourcePublishedAgain,
            SiteBuilderOperationStatus.NotFound => "the page was deleted",
            SiteBuilderOperationStatus.Forbidden => "no builder:read in the store of the page",
            _ => string.IsNullOrEmpty(result.Error) ? $"not marked ({result.Status})" : result.Error.TrimEnd('.'),
        };
    }
}
