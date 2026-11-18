using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;

namespace VirtoCommerce.SiteBuilder.MCP;

// A translation is marked only while the source is still at the revision the agent read.
public class TranslationSources(IMultilanguagePageService multilanguagePageService)
{
    public const string SourcePublishedAgain = "the source language version was published again after you read it";

    // Why the translation cannot be marked, or null.
    public virtual async Task<string> GetProblemAsync(GroupedSiteBuilderPage page, DateTime? sourceRevisionDate, CancellationToken cancellationToken) =>
        (await CheckAsync(page, sourceRevisionDate, cancellationToken)).Problem;

    // Marks the translation at the source revision the agent read; why it was not marked, or null.
    public virtual async Task<string> MarkUpToDateAsync(GroupedSiteBuilderPage page, DateTime? sourceRevisionDate, CancellationToken cancellationToken)
    {
        var (problem, storedRevision) = await CheckAsync(page, sourceRevisionDate, cancellationToken);
        if (problem != null)
        {
            return problem;
        }

        try
        {
            // The stored revision, which the save compares exactly: the agent's may have lost a fraction of a millisecond.
            await multilanguagePageService.MarkUpToDateAsync(page.Id, storedRevision, cancellationToken);
            return null;
        }
        catch (SiteBuilderContentConflictException)
        {
            return SourcePublishedAgain;
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            return ex.Message;
        }
    }

    private async Task<(string Problem, DateTime? SourceRevisionDate)> CheckAsync(GroupedSiteBuilderPage page, DateTime? sourceRevisionDate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(page.MultilanguagePageId))
        {
            return ("it is not a language version of a Multilanguage Page", null);
        }

        if (sourceRevisionDate == null)
        {
            return ("source_revision_date is missing: read the source language version and pass its source_revision_date", null);
        }

        var source = (await multilanguagePageService.GetLanguagesAsync(page.MultilanguagePageId, cancellationToken))
            .FirstOrDefault(x => x.IsDefault)?.Page;

        if (source?.SourceRevisionDate == null)
        {
            return ("the source language version has not been published", null);
        }

        if (source.Id == page.Id)
        {
            return ("it is the source language version, which is never outdated", null);
        }

        return IsSame(source.SourceRevisionDate.Value, sourceRevisionDate.Value)
            ? (null, source.SourceRevisionDate)
            : (SourcePublishedAgain, null);
    }

    // The stored time may lose a fraction of a millisecond on its way through JSON and the database.
    private static bool IsSame(DateTime stored, DateTime given) =>
        Math.Abs((PageLookup.ToUtc(stored).Value - PageLookup.ToUtc(given).Value).TotalMilliseconds) < 1;
}
