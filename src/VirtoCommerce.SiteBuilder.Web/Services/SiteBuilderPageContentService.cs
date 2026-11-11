using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Web.Services;

[method: ActivatorUtilitiesConstructor]
public sealed class SiteBuilderPageContentService(
    ISiteBuilderPageService pageService,
    IGroupedPageService groupedPageService,
    IEventPublisher eventPublisher,
    ILogger<SiteBuilderPageContentService> logger,
    IGroupedPageContentService versionedContent,
    IGroupedPageContentWriter groupedPageContentWriter)
{
    public SiteBuilderPageContentService(
        ISiteBuilderPageService pageService,
        IGroupedPageService groupedPageService,
        IEventPublisher eventPublisher,
        ILogger<SiteBuilderPageContentService> logger)
        : this(pageService, groupedPageService, eventPublisher, logger, groupedPageService as IGroupedPageContentService,
            new GroupedPageContentWriter(groupedPageService as IGroupedPageContentService, pageService, eventPublisher))
    {
    }

    private IGroupedPageContentService VersionedContent => versionedContent
        ?? throw new InvalidOperationException("Supply IGroupedPageContentService when constructing a custom content service.");

    public Task<SiteBuilderContentSnapshot> LoadGroupContentAsync(
        GroupedSiteBuilderPage group, bool draft, CancellationToken cancellationToken)
        => VersionedContent.LoadGroupContentAsync(group, draft, cancellationToken);

    /// <summary>The source page is selected from current membership under the group lock; sourcePageId is retained for compatibility.</summary>
    public async Task<SiteBuilderPageContentWriteResult> SaveGroupUpdateAsync(
        GroupedSiteBuilderPage groupedPage,
        string sourcePageId,
        CancellationToken cancellationToken)
    {
        try
        {
            // The service reads the authoritative source under the group lock; a previously selected
            // sourcePageId can be stale by the time a Shell settings update reaches persistence.
            await UpdateGroupSettingsAsync(null, groupedPage, cancellationToken);
            return SiteBuilderPageContentWriteResult.Success;
        }
        catch (InvalidDataException ex)
        {
            return SiteBuilderPageContentWriteResult.Invalid(ex.Message);
        }
    }

    /// <summary>The draft is selected under the group lock; pageId is retained for compatibility.</summary>
    public async Task UpdateGroupSettingsAsync(
        string pageId,
        GroupedSiteBuilderPage groupedPage,
        CancellationToken cancellationToken)
    {
        await VersionedContent.SaveGroupSettingsAsync(groupedPage, cancellationToken);
    }

    public async Task<SiteBuilderPageContentWriteResult> SaveContentAsync(
        string groupId,
        GroupedSiteBuilderPage groupedPage,
        string content,
        CancellationToken cancellationToken)
    {
        var draft = await GetOrCreatePersistedDraftAsync(groupId, groupedPage);
        var pageId = draft.Page!.Id;
        var errorMessage = await TryWriteContentAsync(
            pageId,
            draft.CreatedPageId,
            () => groupedPageService.SaveContent(pageId, content, cancellationToken));

        if (errorMessage != null)
        {
            return SiteBuilderPageContentWriteResult.Invalid(errorMessage);
        }

        await RaisePageContentChangedAsync(pageId, cancellationToken);

        return SiteBuilderPageContentWriteResult.Success;
    }

    public async Task<SiteBuilderConditionalContentWriteResult> SaveConditionalContentAsync(
        GroupedSiteBuilderPage group, string content, string expectedETag, CancellationToken cancellationToken)
    {
        return await groupedPageContentWriter.SaveContentAsync(group, content, expectedETag, cancellationToken);
    }

    public async Task<SiteBuilderPageContentWriteResult> CopyContentAsync(
        string targetGroupId,
        GroupedSiteBuilderPage targetGroup,
        string sourcePageId,
        CancellationToken cancellationToken)
    {
        var draft = await GetOrCreatePersistedDraftAsync(targetGroupId, targetGroup);
        if (draft.Page == null)
        {
            return SiteBuilderPageContentWriteResult.NotFound;
        }

        var errorMessage = await TryWriteContentAsync(
            draft.Page.Id,
            draft.CreatedPageId,
            () => groupedPageService.CopyPageContentAsync(
                sourcePageId,
                draft.Page.Id,
                cancellationToken));
        if (errorMessage != null)
        {
            return SiteBuilderPageContentWriteResult.Invalid(errorMessage);
        }

        await RaisePageContentChangedAsync(draft.Page.Id, cancellationToken);

        return SiteBuilderPageContentWriteResult.Success;
    }

    private async Task<DraftPage> GetOrCreatePersistedDraftAsync(
        string groupId,
        GroupedSiteBuilderPage groupedPage)
    {
        var draftPage = SiteBuilderPageSelection.Order(groupedPage.Pages).FirstOrDefault(x => x.Status == Draft);
        if (draftPage != null)
        {
            return new DraftPage(draftPage, null);
        }

        var createdDraftPage = CreateDraft(groupedPage.StoreId);
        groupedPage.Pages.Add(createdDraftPage);
        await groupedPageService.SaveChangesAsync([groupedPage]);

        groupedPage = await groupedPageService.GetByIdAsync(groupId);
        draftPage = SiteBuilderPageSelection.Order(groupedPage.Pages).FirstOrDefault(x => x.Status == Draft);

        return new DraftPage(draftPage, createdDraftPage.Id);
    }

    private static SiteBuilderPage CreateDraft(string storeId)
    {
        var draftPage = AbstractTypeFactory<SiteBuilderPage>.TryCreateInstance();
        draftPage.Id = Guid.NewGuid().ToString("N");
        draftPage.StoreId = storeId;
        draftPage.Status = Draft;

        return draftPage;
    }

    private async Task<string> TryWriteContentAsync(
        string pageId,
        string createdDraftPageId,
        Func<Task> writeContent)
    {
        try
        {
            await writeContent();
            return null;
        }
        catch (Exception ex)
        {
            if (createdDraftPageId != null &&
                string.Equals(createdDraftPageId, pageId, StringComparison.OrdinalIgnoreCase))
            {
                await RemoveFailedDraftAsync(pageId, ex);
            }

            if (ex is InvalidDataException)
            {
                return ex.Message;
            }

            throw;
        }
    }

    private async Task RemoveFailedDraftAsync(string pageId, Exception originalException)
    {
        try
        {
            var deleted = await groupedPageService.TryDeleteEmptyDraftAsync(pageId);
            if (!deleted && logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Skipped cleanup of draft page {PageId} after a failed content write because it is no longer an empty draft",
                    pageId);
            }
        }
        catch (Exception cleanupException)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(
                    cleanupException,
                    "Failed to remove draft page {PageId} after its content write failed: {WriteError}",
                    pageId,
                    originalException.Message);
            }
        }
    }

    private async Task RaisePageContentChangedAsync(string pageId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(pageId))
        {
            return;
        }

        var pages = await pageService.GetAsync([pageId]);
        var page = pages.FirstOrDefault();
        if (page == null)
        {
            return;
        }

        var entry = new GenericChangedEntry<SiteBuilderPage>(page, EntryState.Modified);
        await eventPublisher.Publish(new SiteBuilderPageChangedEvent([entry]), cancellationToken);
    }

    private sealed record DraftPage(SiteBuilderPage Page, string CreatedPageId);
}

public sealed record SiteBuilderPageContentWriteResult(bool PageFound, string ErrorMessage)
{
    public static SiteBuilderPageContentWriteResult Success { get; } = new(true, null);
    public static SiteBuilderPageContentWriteResult NotFound { get; } = new(false, null);

    public static SiteBuilderPageContentWriteResult Invalid(string errorMessage)
    {
        return new SiteBuilderPageContentWriteResult(true, errorMessage);
    }
}
