using Microsoft.Extensions.Logging;
using VirtoCommerce.Platform.Core.Jobs;
using VirtoCommerce.SiteBuilder.MCP.Services;

namespace VirtoCommerce.SiteBuilder.MCP.Jobs;

public class UpdateSectionsBulkJobPayload
{
    public string UserId { get; set; }

    public string SectionType { get; set; }

    public string Change { get; set; }

    public string SettingsJson { get; set; }

    public string SettingsLanguage { get; set; }

    public IList<UpdateSectionsBulkJobPage> Pages { get; set; } = [];
}

public class UpdateSectionsBulkJobPage : ISiteBuilderJobPage
{
    public string PageId { get; set; }

    public string VersionToken { get; set; }

    public IList<string> SectionIds { get; set; } = [];
}

public class UpdateSectionsBulkJob(
    SiteBuilderJobUsers jobUsers,
    PageDraftJobRunner runner,
    ILogger<UpdateSectionsBulkJob> logger)
    : IBackgroundJobHandler<UpdateSectionsBulkJobPayload>
{
    public async Task Execute(UpdateSectionsBulkJobPayload payload, IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        var settings = SectionBulkChanges.ReadSettings(payload.SettingsJson);
        var tally = new PageJobTally();
        var total = payload.Pages.Count;

        var stopReason = await jobUsers.RunPagesAsync(payload.UserId, payload.Pages, tally,
            (user, page) => RunAsync(user, page, SectionBulkChanges.CreateOperations(payload.Change, settings, payload.SettingsLanguage, page.SectionIds),
                cancellationToken),
            processed => context.Progress.Report(new JobProgressInfo
            {
                Message = $"{processed} of {total} pages: {tally.Describe()}",
                ProcessedCount = processed,
                TotalCount = total,
            }, cancellationToken),
            cancellationToken);

        await context.Progress.Report(new JobProgressInfo
        {
            Message = stopReason == null
                ? $"Done: {payload.Change} on {payload.SectionType} sections. {tally.Describe()}"
                : SiteBuilderJobUsers.DescribeStop(stopReason, total, tally, tally.Describe()),
            ProcessedCount = tally.Total,
            TotalCount = total,
        }, cancellationToken);
    }

    private async Task<PageJobResult> RunAsync(System.Security.Claims.ClaimsPrincipal user, UpdateSectionsBulkJobPage page,
        IReadOnlyList<PageEditOperation> operations, CancellationToken cancellationToken)
    {
        try
        {
            return await runner.RunAsync(user, page.PageId, page.VersionToken, operations, dryRun: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "update_sections_bulk could not change page {PageId}", page.PageId);
            return new PageJobResult(PageJobOutcome.Failed, "an unexpected error; see the platform log");
        }
    }
}
