using System.Security.Claims;
using Microsoft.Extensions.Logging;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Jobs;
using VirtoCommerce.SiteBuilder.Core.Services;

namespace VirtoCommerce.SiteBuilder.MCP.Jobs;

public class SetTranslationsJobPayload
{
    // The user who called set_translations; the job runs as them.
    public string UserId { get; set; }

    public bool MarkUpToDate { get; set; }

    // One save per page, at the version the agent read.
    public IList<SetTranslationsJobPage> Pages { get; set; } = [];
}

public class SetTranslationsJobPage : ISiteBuilderJobPage
{
    public string PageId { get; set; }

    public string VersionToken { get; set; }

    // The source publication the agent translated from.
    public DateTime? SourceRevisionDate { get; set; }

    public IList<SetTranslationsJobText> Texts { get; set; } = [];
}

public class SetTranslationsJobText
{
    // Null for a setting of the page.
    public string SectionId { get; set; }

    public string SettingPath { get; set; }

    public string Language { get; set; }

    public string Text { get; set; }
}

// Saves the agent's translations page by page as the user who asked for them, and marks a translation up to date only
// while the source is still the publication it was translated from.
public class SetTranslationsJob(
    SiteBuilderJobUsers jobUsers,
    PageDraftJobRunner runner,
    IGroupedPageService groupedPageService,
    TranslationSources translationSources,
    ILogger<SetTranslationsJob> logger)
    : IBackgroundJobHandler<SetTranslationsJobPayload>
{
    public async Task Execute(SetTranslationsJobPayload payload, IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var tally = new PageJobTally();
        var marked = 0;
        var notMarked = new List<string>();
        var total = payload.Pages.Count;

        async Task<PageJobResult> SaveAndMarkAsync(ClaimsPrincipal user, SetTranslationsJobPage page)
        {
            var result = await RunAsync(user, page, cancellationToken);
            if (payload.MarkUpToDate && result.Outcome is PageJobOutcome.Changed or PageJobOutcome.Unchanged)
            {
                var problem = await MarkAsync(page, cancellationToken);
                if (problem == null)
                {
                    marked++;
                }
                else
                {
                    notMarked.Add($"{page.PageId} ({problem})");
                }
            }

            return result;
        }

        var stopReason = await jobUsers.RunPagesAsync(payload.UserId, payload.Pages, tally, SaveAndMarkAsync,
            processed => context.Progress.Report(new JobProgressInfo
            {
                Message = $"{processed} of {total} pages: {Describe(tally, payload.MarkUpToDate, marked, notMarked)}",
                ProcessedCount = processed,
                TotalCount = total,
            }, cancellationToken),
            cancellationToken);

        await context.Progress.Report(new JobProgressInfo
        {
            Message = stopReason == null
                ? $"Done. {Describe(tally, payload.MarkUpToDate, marked, notMarked)}"
                : SiteBuilderJobUsers.DescribeStop(stopReason, total, tally, Describe(tally, payload.MarkUpToDate, marked, notMarked)),
            ProcessedCount = tally.Total,
            TotalCount = total,
        }, cancellationToken);
    }

    private async Task<PageJobResult> RunAsync(ClaimsPrincipal user, SetTranslationsJobPage page, CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync(user, page.PageId, page.VersionToken, TranslationOperations.Create(page.Texts), dryRun: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One page that cannot be saved does not stop the others; the reason stays in the log.
            logger.LogError(ex, "set_translations could not save page {PageId}", page.PageId);
            return new PageJobResult(PageJobOutcome.Failed, "an unexpected error; see the platform log");
        }
    }

    private async Task<string> MarkAsync(SetTranslationsJobPage page, CancellationToken cancellationToken)
    {
        var group = await groupedPageService.GetByIdAsync(page.PageId);
        if (group == null)
        {
            return "the page was deleted";
        }

        return await translationSources.MarkUpToDateAsync(group, page.SourceRevisionDate, cancellationToken);
    }

    private static string Describe(PageJobTally tally, bool markUpToDate, int marked, List<string> notMarked)
    {
        var text = tally.Describe("saved");
        if (!markUpToDate)
        {
            return text;
        }

        text += $" {marked} marked up to date";
        return notMarked.Count == 0 ? text + "." : $"{text}; not marked: {string.Join(", ", notMarked.Take(10))}" +
            (notMarked.Count > 10 ? $" and {notMarked.Count - 10} more." : ".");
    }
}

public static class TranslationOperations
{
    public static IReadOnlyList<PageEditOperation> Create(IEnumerable<SetTranslationsJobText> texts) =>
        texts.Select(x => new PageEditOperation
        {
            Op = string.IsNullOrEmpty(x.SectionId) ? "set_page_settings" : "set_section_settings",
            SectionId = x.SectionId,
            Settings = new() { [x.SettingPath] = System.Text.Json.Nodes.JsonValue.Create(x.Text) },
            Language = x.Language,
        }).ToList();
}
