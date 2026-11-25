using System.Security.Claims;
using Microsoft.Extensions.Logging;
using VirtoCommerce.Platform.Core.Jobs;
using VirtoCommerce.SiteBuilder.MCP.Services;

namespace VirtoCommerce.SiteBuilder.MCP.Jobs;

public class SetTranslationsJobPayload
{
    public string UserId { get; set; }

    public bool MarkUpToDate { get; set; }

    public IList<SetTranslationsJobPage> Pages { get; set; } = [];
}

public class SetTranslationsJobPage : ISiteBuilderJobPage
{
    public string PageId { get; set; }

    public string VersionToken { get; set; }

    public DateTime? SourceRevisionDate { get; set; }

    public IList<SetTranslationsJobText> Texts { get; set; } = [];
}

public class SetTranslationsJobText
{
    public string SectionId { get; set; }

    public string SettingPath { get; set; }

    public string Language { get; set; }

    public string Text { get; set; }
}

public class SetTranslationsJob(
    SiteBuilderJobUsers jobUsers,
    PageDraftJobRunner runner,
    TranslationSources translationSources,
    ILogger<SetTranslationsJob> logger)
    : IBackgroundJobHandler<SetTranslationsJobPayload>
{
    private const int MaxNamedNotMarked = 10;

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
                var problem = await translationSources.MarkUpToDateAsync(user, page.PageId, page.SourceRevisionDate, cancellationToken);
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
            logger.LogError(ex, "set_translations could not save page {PageId}", page.PageId);
            return new PageJobResult(PageJobOutcome.Failed, "an unexpected error; see the platform log");
        }
    }

    private static string Describe(PageJobTally tally, bool markUpToDate, int marked, List<string> notMarked)
    {
        var text = tally.Describe("saved");
        if (!markUpToDate)
        {
            return text;
        }

        text += $" {marked} marked up to date";
        if (notMarked.Count == 0)
        {
            return text + ".";
        }

        var more = notMarked.Count > MaxNamedNotMarked ? $" and {notMarked.Count - MaxNamedNotMarked} more." : ".";
        return $"{text}; not marked: {string.Join(", ", notMarked.Take(MaxNamedNotMarked))}" + more;
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
