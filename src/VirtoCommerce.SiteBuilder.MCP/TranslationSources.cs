using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;

namespace VirtoCommerce.SiteBuilder.MCP;

// Whether a translation may be marked up to date: it is a language version, not the source, and the source has not
// been published since the agent read it. The source revision is the one MultilanguagePageService.MarkUpToDateAsync
// copies onto the translation: the SourceRevisionDate of the default language version.
public class TranslationSources(IMultilanguagePageService multilanguagePageService)
{
    // Why the translation cannot be marked, or null.
    public virtual async Task<string> GetProblemAsync(GroupedSiteBuilderPage page, DateTime? sourceRevisionDate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(page.MultilanguagePageId))
        {
            return "it is not a language version of a Multilanguage Page";
        }

        if (sourceRevisionDate == null)
        {
            return "source_revision_date is missing: read the source language version and pass its source_revision_date";
        }

        var source = (await multilanguagePageService.GetLanguagesAsync(page.MultilanguagePageId, cancellationToken))
            .FirstOrDefault(x => x.IsDefault)?.Page;

        if (source == null || source.SourceRevisionDate == null)
        {
            return "the source language version has not been published";
        }

        if (source.Id == page.Id)
        {
            return "it is the source language version, which is never outdated";
        }

        return IsSame(source.SourceRevisionDate.Value, sourceRevisionDate.Value)
            ? null
            : "the source language version was published again after you read it";
    }

    public virtual Task MarkUpToDateAsync(GroupedSiteBuilderPage page, CancellationToken cancellationToken) =>
        multilanguagePageService.MarkUpToDateAsync(page.Id, cancellationToken);

    // The stored time may lose a fraction of a millisecond on its way through JSON and the database.
    private static bool IsSame(DateTime stored, DateTime given) =>
        Math.Abs((ToUtc(stored) - ToUtc(given)).TotalMilliseconds) < 1;

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
