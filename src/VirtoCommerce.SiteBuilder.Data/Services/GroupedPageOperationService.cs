using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core.Common;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Data.Services;

[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The services come from dependency injection; a parameter object would only hide them and change the constructor that subclasses call.")]
public class GroupedPageOperationService(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    ISiteBuilderPageService pageService,
    SiteBuilderPageContentService pageContentService,
    ISiteBuilderSharedComponentPlacementValidator sharedComponentPlacementValidator,
    IDesignerContentService designerContentService,
    ISiteBuilderContentGuard contentGuard,
    IContentHistory contentHistory,
    ISiteBuilderPublishConflictService publishConflictService,
    ISchemaCatalogService schemaCatalogService) : IGroupedPageOperationService
{
    private const string PageRequiredError = "The page is missing or invalid.";
    private const string CreateFieldsRequiredError = "StoreId, Name and Content are required.";
    private const string ContentRequiredError = "Content is required.";
    private const string MalformedContentError = "Page content must be a non-empty, well-formed JSON document.";
    private const string DraftNotFoundError = "Draft page not found.";
    private const string PublishedNotFoundError = "Published page not found.";
    private const string UnpublishWithChangesError = "Can't unpublish a page that has changes.";
    private const string ArchivedUpdateError = "Archived page cannot be updated.";
    private const string ConflictingVersionsError = "If-Match and the body ETag must identify the same content version.";
    private const string VersionRequiredError = "Read the page content and send its ETag in If-Match or in the content-json body before saving.";
    private const string MalformedVersionError = "If-Match must contain the single strong ETag returned by the content GET.";
    private const string PageName = "page";

    public virtual Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> CreateAsync(GroupedPageSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return CreateInternalAsync(request, user);
    }

    private async Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> CreateInternalAsync(GroupedPageSaveOperationRequest request, ClaimsPrincipal user)
    {
        var model = request.Page;
        if (model == null)
        {
            return SiteBuilderOperationResult.Invalid(PageRequiredError).As<GroupedSiteBuilderPage>();
        }

        if ((CheckPermalink(model.Permalink) ?? CheckName(model.Name) ?? await authorizationService.CheckStoreOperationAsync(user, model)) is { } refused)
        {
            return refused.As<GroupedSiteBuilderPage>();
        }

        // A page becomes a language version only through the multilanguage page endpoints, which check the root.
        model.MultilanguagePageId = null;
        model.OverriddenFields = null;
        model.SourceRevisionDate = null;

        if (request.DryRun)
        {
            var preview = CreatePreview();
            preview.AddPage(from: null, model);
            return SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(preview);
        }

        var draftPage = AbstractTypeFactory<SiteBuilderPage>.TryCreateInstance();

        draftPage.Id = null;
        draftPage.Status = Draft; // always create a new page in draft status
        draftPage.StoreId = model.StoreId;
        draftPage.Content = SiteBuilderContentSettings.Synchronize(null, model);

        // Pages sent by the client are ignored: a page is published only through PublishAsync, which checks permalink conflicts
        model.Pages = [draftPage];

        await groupedPageService.SaveChangesAsync([model]);

        return SiteBuilderOperationResult.Success(model);
    }

    public virtual Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> CreateWithContentAsync(GroupedPageCreateOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return CreateWithContentInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> CreateWithContentInternalAsync(GroupedPageCreateOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var model = request.Page;
        if (CheckCreateRequest(model) is { } invalid)
        {
            return invalid.As<GroupedSiteBuilderPage>();
        }

        if ((await authorizationService.CheckStoreOperationAsync(user, model)
            ?? await GuardCreateContentAsync(model, cancellationToken)
            ?? await CheckSharedComponentsAsync(user, model.StoreId, model.Content, cancellationToken)) is { } refused)
        {
            return refused.As<GroupedSiteBuilderPage>();
        }

        var groupedPage = CreateGroup(model);
        if (request.DryRun)
        {
            var preview = CreatePreview();
            preview.AddPage(from: null, groupedPage).AddField(ModuleConstants.ChangePreview.Content, from: null, model.Content);
            return SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(preview);
        }

        return await SaveNewGroupAsync(groupedPage, model.Content);
    }

    /// <summary>The fields the page is created with.</summary>
    private static SiteBuilderOperationResult CheckCreateRequest(CreateGroupedPageRequest model)
    {
        if (!HasCreateFields(model))
        {
            return SiteBuilderOperationResult.Invalid(CreateFieldsRequiredError);
        }

        return CheckPermalink(model.Permalink) ?? CheckName(model.Name) ?? CheckEnvelope(model.Content);
    }

    /// <summary>
    /// Checks the content against the store's current catalog, and sanitizes it in place so that what is written is sanitized.
    /// After the store access check: the catalog is of a store the user may reach.
    /// </summary>
    protected virtual async Task<SiteBuilderOperationResult> GuardCreateContentAsync(CreateGroupedPageRequest model, CancellationToken cancellationToken)
    {
        var catalog = await schemaCatalogService.GetCatalogObjectAsync(model.StoreId, hash: null, cancellationToken);
        try
        {
            model.Content = contentGuard.GuardPageContent(model.Content, PageName, catalog);
            return null;
        }
        catch (InvalidDataException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message);
        }
    }

    private static bool HasCreateFields(CreateGroupedPageRequest model) =>
        model != null &&
        !string.IsNullOrWhiteSpace(model.StoreId) &&
        !string.IsNullOrWhiteSpace(model.Name) &&
        !string.IsNullOrWhiteSpace(model.Content);

    private static GroupedSiteBuilderPage CreateGroup(CreateGroupedPageRequest model)
    {
        var groupedPage = AbstractTypeFactory<GroupedSiteBuilderPage>.TryCreateInstance();
        groupedPage.StoreId = model.StoreId;
        groupedPage.Name = model.Name;
        groupedPage.Permalink = model.Permalink;
        groupedPage.CultureName = model.CultureName;
        groupedPage.OrganizationId = model.OrganizationId;
        groupedPage.Visibility = model.Visibility;
        groupedPage.UserGroups = model.UserGroups;
        groupedPage.StartDate = model.StartDate;
        groupedPage.EndDate = model.EndDate;

        return groupedPage;
    }

    private async Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> SaveNewGroupAsync(GroupedSiteBuilderPage groupedPage, string content)
    {
        var draftPage = AbstractTypeFactory<SiteBuilderPage>.TryCreateInstance();
        draftPage.Status = Draft;
        draftPage.StoreId = groupedPage.StoreId;
        // Assigning Content before SaveChangesAsync ensures the page row and its PageContent
        // are persisted in a single INSERT (EF table-splitting), and the synchronous Changed
        // event sees the content already committed.
        draftPage.Content = content;
        groupedPage.Pages.Add(draftPage);

        try
        {
            await groupedPageService.SaveChangesAsync([groupedPage]);
        }
        catch (InvalidDataException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message).As<GroupedSiteBuilderPage>();
        }

        return SiteBuilderOperationResult.Success(groupedPage);
    }

    public virtual Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> UpdateAsync(GroupedPageSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UpdateInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> UpdateInternalAsync(GroupedPageSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        // A body that does not bind (e.g. userGroups sent as an array instead of a comma-separated string) arrives as null.
        var model = request.Page;
        if (model == null)
        {
            return SiteBuilderOperationResult.Invalid(PageRequiredError).As<GroupedSiteBuilderPage>();
        }

        var groupedPage = await groupedPageService.GetByIdAsync(model.Id);
        var storeChanged = HasStoreChanged(groupedPage, model);
        if ((await CheckGroupUpdateAccessAsync(groupedPage, model, storeChanged, user) ?? CheckGroupUpdate(groupedPage, model)) is { } refused)
        {
            return refused.As<GroupedSiteBuilderPage>();
        }

        var stored = groupedPage?.CloneTyped();
        groupedPage ??= AbstractTypeFactory<GroupedSiteBuilderPage>.TryCreateInstance();
        ApplyGroupChanges(groupedPage, model, storeChanged);

        var sourcePageId = GetPublishedContentSourceId(groupedPage);
        if (await CheckSourceContentAsync(groupedPage, sourcePageId, user, cancellationToken) is { } refusedContent)
        {
            return refusedContent.As<GroupedSiteBuilderPage>();
        }

        var preview = CreatePreview();
        var change = preview.AddPage(stored, groupedPage);
        if (request.DryRun)
        {
            return PreviewGroupUpdate(preview, change, stored);
        }

        var affectsPublished = request.ReportImpact && preview.AffectsPublished;
        return GetSaveCondition(request) is { } condition
            ? await SaveGroupUpdateAsync(groupedPage, condition, affectsPublished, cancellationToken)
            : await SaveGroupUpdateAsync(groupedPage, sourcePageId, affectsPublished, cancellationToken);
    }

    /// <summary>The shared components of the content the update copies into a new draft; <c>null</c> when it copies none.</summary>
    private async Task<SiteBuilderOperationResult> CheckSourceContentAsync(GroupedSiteBuilderPage groupedPage, string sourcePageId, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (sourcePageId == null)
        {
            return null;
        }

        var sourceContent = await groupedPageService.LoadContent(sourcePageId, cancellationToken);
        return await CheckSharedComponentsAsync(user, groupedPage.StoreId, sourceContent, cancellationToken);
    }

    private static SiteBuilderOperationResult<GroupedSiteBuilderPage> PreviewGroupUpdate(SiteBuilderChangePreview preview, SiteBuilderObjectChange change,
        GroupedSiteBuilderPage stored)
    {
        if (change != null && stored != null)
        {
            // the version a caller sends back to update only the settings it reviewed
            change.ETag = SiteBuilderContentVersion.CreateForGroupSettings(stored);
        }

        return SiteBuilderOperationResult.DryRun<GroupedSiteBuilderPage>(preview);
    }

    /// <summary>The condition the request sets on the save; <c>null</c> when it sets none.</summary>
    private static GroupedPageSaveCondition GetSaveCondition(GroupedPageSaveOperationRequest request)
    {
        if (!request.RequireUnpublished && request.ExpectedSettingsVersion == null)
        {
            return null;
        }

        return new GroupedPageSaveCondition { RequireUnpublished = request.RequireUnpublished, ExpectedSettingsVersion = request.ExpectedSettingsVersion };
    }

    /// <summary>The condition is read under the lock of the save, against the settings as they are stored then.</summary>
    private async Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> SaveGroupUpdateAsync(GroupedSiteBuilderPage groupedPage,
        GroupedPageSaveCondition condition, bool affectsPublished, CancellationToken cancellationToken)
    {
        try
        {
            var writeResult = await pageContentService.SaveGroupUpdateAsync(groupedPage, condition, cancellationToken);
            return writeResult.ErrorMessage != null
                ? SiteBuilderOperationResult.Invalid(writeResult.ErrorMessage).As<GroupedSiteBuilderPage>()
                : SiteBuilderOperationResult.Success(groupedPage, affectsPublished);
        }
        catch (SiteBuilderContentConflictException ex)
        {
            return SiteBuilderOperationResult.Conflict(ex.Message).As<GroupedSiteBuilderPage>();
        }
    }

    private async Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> SaveGroupUpdateAsync(GroupedSiteBuilderPage groupedPage, string sourcePageId,
        bool affectsPublished, CancellationToken cancellationToken)
    {
        var writeResult = await pageContentService.SaveGroupUpdateAsync(groupedPage, sourcePageId, cancellationToken);
        if (writeResult.ErrorMessage != null)
        {
            return SiteBuilderOperationResult.Invalid(writeResult.ErrorMessage).As<GroupedSiteBuilderPage>();
        }

        return SiteBuilderOperationResult.Success(groupedPage, affectsPublished);
    }

    public virtual Task<SiteBuilderOperationResult> PublishAsync(GroupedPagePublishOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PublishInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult> PublishInternalAsync(GroupedPagePublishOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var (groupedPage, refused) = await GetGroupAsync(request.GroupId, user);
        if (refused != null)
        {
            return refused;
        }

        return request.Publish
            ? await PublishDraftLockedAsync(groupedPage, request, user, cancellationToken)
            : await UnpublishAsync(groupedPage, request, cancellationToken);
    }

    /// <summary>
    /// Reloads the page under the lock: another publish may have changed it while this one waited. A dry run writes nothing, so it takes no lock.
    /// </summary>
    private Task<SiteBuilderOperationResult> PublishDraftLockedAsync(GroupedSiteBuilderPage groupedPage, GroupedPagePublishOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (request.DryRun)
        {
            return PublishDraftAsync(groupedPage, request, user, cancellationToken);
        }

        return publishConflictService.ExecuteExclusiveAsync(groupedPage, async () =>
        {
            var currentPage = await groupedPageService.GetByIdAsync(groupedPage.Id);

            return currentPage == null
                ? SiteBuilderOperationResult.NotFound()
                : await PublishDraftAsync(currentPage, request, user, cancellationToken);
        }, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult> PublishDraftAsync(GroupedSiteBuilderPage groupedPage, GroupedPagePublishOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (GroupedPagePublishing.GetDraft(groupedPage) == null)
        {
            return SiteBuilderOperationResult.Invalid(DraftNotFoundError);
        }

        var (conflicts, conflictingGroups) = await publishConflictService.FindBlockingConflictsAsync(groupedPage);
        if (await CheckPermalinkConflictsAsync(conflicts, conflictingGroups, request.UnpublishConflicting, user) is { } refused)
        {
            return refused;
        }

        if (request.DryRun)
        {
            var dryRun = await PreviewPublishingAsync(groupedPage, Draft, Published, cancellationToken);
            foreach (var conflictingGroup in conflictingGroups)
            {
                var published = await pageContentService.LoadGroupContentAsync(conflictingGroup, draft: false, cancellationToken);
                dryRun.Preview.AddPublishing(conflictingGroup, Published, Draft).ETag = published.ETag;
            }

            return dryRun;
        }

        if (request.ExpectedSettingsVersion != null &&
            !string.Equals(SiteBuilderContentVersion.CreateForGroupSettings(groupedPage), request.ExpectedSettingsVersion.Trim(), StringComparison.Ordinal))
        {
            return SiteBuilderOperationResult.Conflict(SiteBuilderContentConflictException.SettingsChangedMessage);
        }

        if (request.ExpectedReplacedVersions != null &&
            !await AreReplacedAsReviewedAsync(conflictingGroups, request.ExpectedReplacedVersions, cancellationToken))
        {
            return SiteBuilderOperationResult.Conflict(SiteBuilderContentConflictException.PageChangedMessage);
        }

        GroupedPagePublishing.TryPublishDraft(groupedPage, out var obsoletePageIds);
        var pagesToDelete = obsoletePageIds.ToList();
        pagesToDelete.AddRange(conflictingGroups.SelectMany(UnpublishConflictingGroup));
        var publishedPageId = groupedPage.Pages.Single(x => !obsoletePageIds.Contains(x.Id)).Id;
        if (await SavePublishingAsync(groupedPage, conflictingGroups, pagesToDelete, draft: true, request.ExpectedDraftVersion, request.ExpectedSettingsVersion,
            cancellationToken) is { } conflict)
        {
            return conflict;
        }

        if (publishedPageId != null)
        {
            // load by id: the group still lists the published row deleted above
            var publishedContent = await groupedPageService.LoadContent(publishedPageId, cancellationToken);
            await RecordHistoryAsync(groupedPage, publishedContent, ModuleConstants.GitHistory.PublishedSummary, request.SaveId, user, cancellationToken);
        }

        return SiteBuilderOperationResult.Success(affectsPublished: request.ReportImpact);
    }

    private async Task<SiteBuilderOperationResult> UnpublishAsync(GroupedSiteBuilderPage groupedPage, GroupedPagePublishOperationRequest request,
        CancellationToken cancellationToken)
    {
        var pageToUnpublish = SiteBuilderPageSelection.Order(groupedPage.Pages).FirstOrDefault(x => x.Status == Published);
        if (pageToUnpublish == null)
        {
            return SiteBuilderOperationResult.Invalid(PublishedNotFoundError);
        }

        var hasDraft = groupedPage.Pages.Any(x => x.Status == Draft);
        if (hasDraft)
        {
            return SiteBuilderOperationResult.Invalid(UnpublishWithChangesError);
        }

        if (request.DryRun)
        {
            return await PreviewPublishingAsync(groupedPage, Published, Draft, cancellationToken);
        }

        pageToUnpublish.Status = Draft;
        var pagesToDelete = groupedPage.Pages.Where(x => x.Id != pageToUnpublish.Id).Select(x => x.Id).ToList();

        return await SavePublishingAsync(groupedPage, [], pagesToDelete, draft: false, request.ExpectedPublishedVersion, expectedSettingsVersion: null,
            cancellationToken)
            ?? SiteBuilderOperationResult.Success(affectsPublished: request.ReportImpact);
    }

    /// <param name="unpublishedGroups">
    /// Published pages turned back into drafts with the page: in the same save, or right after the page is saved at its version.
    /// </param>
    /// <param name="draft">The version is of the draft, else of the published page.</param>
    /// <param name="expectedVersion">The version the page has to be at, compared under the lock of the save; <c>null</c> saves it whatever it holds.</param>
    /// <returns>The conflict when the page is at another version, with nothing written; <c>null</c> when it is saved.</returns>
    private async Task<SiteBuilderOperationResult> SavePublishingAsync(GroupedSiteBuilderPage groupedPage, IList<GroupedSiteBuilderPage> unpublishedGroups,
        List<string> pagesToDelete, bool draft, string expectedVersion, string expectedSettingsVersion, CancellationToken cancellationToken)
    {
        if (expectedVersion == null)
        {
            await groupedPageService.SaveChangesAsync([groupedPage, .. unpublishedGroups]);
        }
        else
        {
            try
            {
                await pageContentService.SaveGroupAtVersionAsync(groupedPage, draft, expectedVersion.Trim(), expectedSettingsVersion?.Trim(), cancellationToken);
            }
            catch (SiteBuilderContentConflictException ex)
            {
                return SiteBuilderOperationResult.Conflict(ex.Message);
            }

            if (unpublishedGroups.Count > 0)
            {
                await groupedPageService.SaveChangesAsync(unpublishedGroups);
            }
        }

        await pageService.DeleteAsync(pagesToDelete);

        return null;
    }

    /// <returns>The refusal when publishing would replace other published pages that are not to be, or cannot be, unpublished; <c>null</c> to go on.</returns>
    private async Task<SiteBuilderOperationResult> CheckPermalinkConflictsAsync(IList<SiteBuilderPermalinkUsage> conflicts,
        IList<GroupedSiteBuilderPage> conflictingGroups, bool unpublishConflicting, ClaimsPrincipal user)
    {
        var hasUnpublishedChanges = conflicts.Any(x => !x.CanUnpublish);
        if (conflicts.Count > 0 && (!unpublishConflicting || hasUnpublishedChanges))
        {
            return PermalinkConflict(conflicts, hasUnpublishedChanges);
        }

        return conflictingGroups.Count > 0 ? await authorizationService.CheckStoreOperationAsync(user, conflictingGroups) : null;
    }

    private async Task<bool> AreReplacedAsReviewedAsync(IList<GroupedSiteBuilderPage> conflictingGroups, IDictionary<string, string> expectedVersions,
        CancellationToken cancellationToken)
    {
        if (conflictingGroups.Count != expectedVersions.Count)
        {
            return false;
        }

        foreach (var group in conflictingGroups)
        {
            var published = await pageContentService.LoadGroupContentAsync(group, draft: false, cancellationToken);
            if (!expectedVersions.TryGetValue(group.Id, out var eTag) || !string.Equals(eTag?.Trim(), published.ETag, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // Turns the published page of a group without a draft back into a draft, like unpublishing does, and returns
    // the ids of the group's other pages to delete. A group unpublished meanwhile is left as it is.
    private static IEnumerable<string> UnpublishConflictingGroup(GroupedSiteBuilderPage group)
    {
        var pageToUnpublish = SiteBuilderPageSelection.Order(group.Pages).FirstOrDefault(x => x.Status == Published);
        if (pageToUnpublish == null)
        {
            return [];
        }

        pageToUnpublish.Status = Draft;
        return group.Pages.Where(x => x.Id != pageToUnpublish.Id).Select(x => x.Id).ToList();
    }

    private static SiteBuilderOperationResult<SiteBuilderPermalinkConflictResult> PermalinkConflict(IList<SiteBuilderPermalinkUsage> conflicts,
        bool hasUnpublishedChanges)
    {
        var names = string.Join(", ", conflicts.Select(x => $"'{x.Name}'"));
        var error = hasUnpublishedChanges
            ? $"Pages {names} are published with the same permalink for the same visitors, and some of them have unpublished changes. Publish or discard those changes first."
            : $"Pages {names} are published with the same permalink for the same visitors. Unpublish them first, or publish with unpublishConflicting=true.";

        var result = AbstractTypeFactory<SiteBuilderPermalinkConflictResult>.TryCreateInstance();
        result.Error = error;
        result.Conflicts = conflicts;

        return SiteBuilderOperationResult.PermalinkConflict(result);
    }

    public virtual Task<SiteBuilderOperationResult> DiscardDraftAsync(GroupedPageDiscardOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return DiscardDraftInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult> DiscardDraftInternalAsync(GroupedPageDiscardOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var (groupedPage, refused) = await GetGroupAsync(request.GroupId, user);
        if (refused != null)
        {
            return refused;
        }

        try
        {
            if (request.DryRun)
            {
                return SiteBuilderOperationResult.DryRun(await designerContentService.PreviewDiscardPageAsync(groupedPage, cancellationToken));
            }

            var command = AbstractTypeFactory<DesignerDiscardCommand>.TryCreateInstance();
            command.StoreId = groupedPage.StoreId;
            command.SaveId = request.SaveId;
            command.Group = groupedPage;
            command.PageETag = request.ExpectedDraftVersion;
            await designerContentService.DiscardAsync(command, user, cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message);
        }
        catch (SiteBuilderContentConflictException ex)
        {
            return SiteBuilderOperationResult.Conflict(ex.Message);
        }

        return SiteBuilderOperationResult.Success();
    }

    public virtual Task<SiteBuilderOperationResult<UpdatePageContentResult>> SaveContentAsync(GroupedPageContentSaveOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SaveContentInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<UpdatePageContentResult>> SaveContentInternalAsync(GroupedPageContentSaveOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var content = request.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return SiteBuilderOperationResult.Invalid(ContentRequiredError).As<UpdatePageContentResult>();
        }

        if (CheckEnvelope(content) is { } invalid)
        {
            return invalid.As<UpdatePageContentResult>();
        }

        var (groupedPage, refused) = await GetGroupAsync(request.GroupId, user);
        if ((refused ?? await CheckSharedComponentsAsync(user, groupedPage.StoreId, content, cancellationToken)) is { } refusedPage)
        {
            return refusedPage.As<UpdatePageContentResult>();
        }

        return await SaveCheckedContentAsync(groupedPage, request, content, user, cancellationToken);
    }

    public virtual Task<SiteBuilderOperationResult<UpdatePageContentResult>> SaveRawContentAsync(GroupedPageContentSaveOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SaveRawContentInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<UpdatePageContentResult>> SaveRawContentInternalAsync(GroupedPageContentSaveOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var (groupedPage, refused) = await GetGroupAsync(request.GroupId, user);
        if (refused != null)
        {
            return refused.As<UpdatePageContentResult>();
        }

        var content = request.ContentReader == null ? request.Content : await request.ContentReader(cancellationToken);
        if (string.IsNullOrWhiteSpace(content) || !IsWellFormedJson(content))
        {
            return SiteBuilderOperationResult.Invalid(MalformedContentError).As<UpdatePageContentResult>();
        }

        if (await CheckSharedComponentsAsync(user, groupedPage.StoreId, content, cancellationToken) is { } refusedContent)
        {
            return refusedContent.As<UpdatePageContentResult>();
        }

        return await SaveCheckedContentAsync(groupedPage, request, content, user, cancellationToken);
    }

    public virtual Task<SiteBuilderOperationResult> CopyContentAsync(GroupedPageContentCopyOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return CopyContentInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult> CopyContentInternalAsync(GroupedPageContentCopyOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var sourceGroup = await groupedPageService.GetByIdAsync(request.SourceGroupId);
        var targetGroup = await groupedPageService.GetByIdAsync(request.GroupId);

        if (sourceGroup == null || targetGroup == null)
        {
            return SiteBuilderOperationResult.NotFound();
        }

        var sourceRefused = await authorizationService.CheckStoreOperationAsync(user, sourceGroup);
        var targetRefused = await authorizationService.CheckStoreOperationAsync(user, targetGroup);
        if ((sourceRefused ?? targetRefused) is { } refused)
        {
            return refused;
        }

        var sourcePageId = GetCopySourcePageId(sourceGroup);
        if (sourcePageId == null)
        {
            return SiteBuilderOperationResult.NotFound();
        }

        var sourceContent = await groupedPageService.LoadContent(sourcePageId, cancellationToken);
        if (await CheckSharedComponentsAsync(user, targetGroup.StoreId, sourceContent, cancellationToken) is { } refusedContent)
        {
            return refusedContent;
        }

        if (request.DryRun)
        {
            var current = await pageContentService.LoadGroupContentAsync(targetGroup, draft: true, cancellationToken);
            var preview = CreatePreview();
            preview.AddObject(ModuleConstants.ChangePreview.Page, targetGroup.Id, current.ETag)
                .AddField(ModuleConstants.ChangePreview.Content, current.Content, sourceContent);
            return SiteBuilderOperationResult.DryRun(preview);
        }

        var writeResult = await pageContentService.CopyContentAsync(request.GroupId, targetGroup, sourcePageId, cancellationToken);
        if (!writeResult.PageFound)
        {
            return SiteBuilderOperationResult.NotFound();
        }

        return writeResult.ErrorMessage != null ? SiteBuilderOperationResult.Invalid(writeResult.ErrorMessage) : SiteBuilderOperationResult.Success();
    }

    /// <returns>The group, and the refusal when it does not exist or the user may not reach its store.</returns>
    protected virtual async Task<(GroupedSiteBuilderPage Group, SiteBuilderOperationResult Refused)> GetGroupAsync(string groupId, ClaimsPrincipal user)
    {
        var groupedPage = await groupedPageService.GetByIdAsync(groupId);
        if (groupedPage == null)
        {
            return (null, SiteBuilderOperationResult.NotFound());
        }

        return (groupedPage, await authorizationService.CheckStoreOperationAsync(user, groupedPage));
    }

    protected virtual async Task<SiteBuilderOperationResult> CheckGroupUpdateAccessAsync(GroupedSiteBuilderPage groupedPage, GroupedSiteBuilderPage model,
        bool storeChanged, ClaimsPrincipal user)
    {
        // Authorize the persisted resource first. Authorizing only the incoming model would let a caller spoof
        // a store they can access and then learn the existing group's store from validation details.
        if (await authorizationService.CheckStoreOperationAsync(user, groupedPage ?? model) is { } forbidden)
        {
            return forbidden;
        }

        // A move crosses two resource boundaries: the caller must be allowed to update both the persisted
        // source and the requested destination store.
        return storeChanged ? await authorizationService.CheckStoreOperationAsync(user, model) : null;
    }

    protected virtual async Task<SiteBuilderOperationResult> CheckSharedComponentsAsync(ClaimsPrincipal user, string storeId, string content,
        CancellationToken cancellationToken)
    {
        var validation = await sharedComponentPlacementValidator.ValidateAsync(user, storeId, content, cancellationToken);
        if (validation.IsForbidden)
        {
            return SiteBuilderOperationResult.Forbidden();
        }

        return validation.Error != null ? SiteBuilderOperationResult.Invalid(validation.Error) : null;
    }

    protected virtual async Task<SiteBuilderOperationResult<UpdatePageContentResult>> SaveCheckedContentAsync(GroupedSiteBuilderPage group,
        GroupedPageContentSaveOperationRequest request, string content, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (HasConflictingContentVersions(request.IfMatch, request.ETag))
        {
            return SiteBuilderOperationResult.Invalid(ConflictingVersionsError).As<UpdatePageContentResult>();
        }

        var expectedETag = (string.IsNullOrWhiteSpace(request.IfMatch) ? request.ETag : request.IfMatch)?.Trim();
        if (string.IsNullOrEmpty(expectedETag))
        {
            return SiteBuilderOperationResult.VersionRequired(VersionRequiredError).As<UpdatePageContentResult>();
        }

        // Require one concrete strong version. A wildcard does not protect an author's work.
        if (!SiteBuilderContentVersion.IsWellFormed(expectedETag))
        {
            return SiteBuilderOperationResult.Invalid(MalformedVersionError).As<UpdatePageContentResult>();
        }

        try
        {
            // here, not only in the writer: the history records what is stored
            var catalog = await schemaCatalogService.GetCatalogObjectAsync(group.StoreId, hash: null, cancellationToken);
            content = contentGuard.GuardPageContent(content, PageName, catalog);
            if (request.DryRun)
            {
                // the version is compared under the lock of the write; the preview names the current one
                var current = await pageContentService.LoadGroupContentAsync(group, draft: true, cancellationToken);
                var preview = CreatePreview();
                preview.AddObject(ModuleConstants.ChangePreview.Page, group.Id, current.ETag)
                    .AddField(ModuleConstants.ChangePreview.Content, current.Content, content);
                return SiteBuilderOperationResult.DryRun<UpdatePageContentResult>(preview);
            }

            var result = await pageContentService.SaveConditionalContentAsync(group, content, expectedETag, cancellationToken);
            if (result.ContentWritten)
            {
                await RecordHistoryAsync(group, content, ModuleConstants.GitHistory.SavedSummary, request.SaveId, user, cancellationToken);
            }

            var version = AbstractTypeFactory<UpdatePageContentResult>.TryCreateInstance();
            version.ETag = result.ETag;
            return SiteBuilderOperationResult.Success(version);
        }
        catch (SiteBuilderContentConflictException ex)
        {
            return SiteBuilderOperationResult.Conflict(ex.Message).As<UpdatePageContentResult>();
        }
        catch (KeyNotFoundException)
        {
            return SiteBuilderOperationResult.NotFound().As<UpdatePageContentResult>();
        }
        catch (InvalidDataException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message).As<UpdatePageContentResult>();
        }
    }

    protected virtual Task RecordHistoryAsync(GroupedSiteBuilderPage group, string content, string description, string saveId, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var record = ContentHistoryRecord.Create(description, saveId, user);
        record.AddEntry(new HistoryItem(HistoryItemType.Page, group.Id), content);

        return contentHistory.RecordAsync(group.StoreId, record, cancellationToken);
    }

    protected virtual SiteBuilderChangePreview CreatePreview() => AbstractTypeFactory<SiteBuilderChangePreview>.TryCreateInstance();

    /// <summary>With the version of the page a publish (from a draft) or an unpublish takes, which the caller sends back to ship only what it reviewed.</summary>
    private async Task<SiteBuilderOperationResult> PreviewPublishingAsync(GroupedSiteBuilderPage group, string from, string to,
        CancellationToken cancellationToken)
    {
        var current = await pageContentService.LoadGroupContentAsync(group, draft: from == Draft, cancellationToken);
        var preview = CreatePreview();
        preview.AddPublishing(group, from, to).ETag = current.ETag;

        return SiteBuilderOperationResult.DryRun(preview);
    }

    private static SiteBuilderOperationResult CheckPermalink(string permalink) =>
        SiteBuilderPermalink.IsValid(permalink) ? null : SiteBuilderOperationResult.Invalid(SiteBuilderPermalink.InvalidMessage);

    private static SiteBuilderOperationResult CheckName(string name) =>
        SiteBuilderPageName.IsValid(name) ? null : SiteBuilderOperationResult.Invalid(SiteBuilderPageName.InvalidMessage);

    private static SiteBuilderOperationResult CheckEnvelope(string content) =>
        PageContentEnvelope.TryValidate(content, out var envelopeError) ? null : SiteBuilderOperationResult.Invalid(envelopeError);

    private static SiteBuilderOperationResult CheckGroupUpdate(GroupedSiteBuilderPage groupedPage, GroupedSiteBuilderPage model)
    {
        if (groupedPage?.Status == Archived)
        {
            return SiteBuilderOperationResult.Invalid(ArchivedUpdateError);
        }

        // An unchanged permalink stored before validation existed must not block editing the page's other fields.
        return (groupedPage?.Permalink == model.Permalink ? null : CheckPermalink(model.Permalink)) ?? CheckName(model.Name);
    }

    private static bool HasStoreChanged(GroupedSiteBuilderPage groupedPage, GroupedSiteBuilderPage model)
    {
        return groupedPage != null &&
            !string.Equals(groupedPage.StoreId, model.StoreId, StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyGroupChanges(GroupedSiteBuilderPage groupedPage, GroupedSiteBuilderPage model, bool storeChanged)
    {
        groupedPage.Name = model.Name;
        groupedPage.Permalink = model.Permalink;
        groupedPage.CultureName = model.CultureName;
        groupedPage.StoreId = model.StoreId;

        if (storeChanged)
        {
            foreach (var page in groupedPage.Pages)
            {
                page.StoreId = model.StoreId;
            }
        }

        groupedPage.Visibility = model.Visibility;
        groupedPage.UserGroups = model.UserGroups;
        groupedPage.StartDate = model.StartDate;
        groupedPage.EndDate = model.EndDate;
        groupedPage.OrganizationId = model.OrganizationId;
    }

    private static string GetPublishedContentSourceId(GroupedSiteBuilderPage groupedPage)
    {
        return groupedPage.Pages.Any(x => x.Status == Draft)
            ? null
            : SiteBuilderPageSelection.Order(groupedPage.Pages).FirstOrDefault(x => x.Status == Published)?.Id;
    }

    private static string GetCopySourcePageId(GroupedSiteBuilderPage sourceGroup)
    {
        return sourceGroup.Pages.Where(x => x.Status == Draft || x.Status == Published)
            .OrderByDescending(x => x.ModifiedDate)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => x.Id)
            .FirstOrDefault();
    }

    private static bool HasConflictingContentVersions(string headerVersion, string bodyVersion)
    {
        return !string.IsNullOrWhiteSpace(headerVersion) && !string.IsNullOrWhiteSpace(bodyVersion) &&
            !string.Equals(headerVersion.Trim(), bodyVersion.Trim(), StringComparison.Ordinal);
    }

    // Only well-formedness is checked, not the document's shape: content stored by older designer versions can
    // be an array rather than the current { settings, content } object, and both still load. What this rules
    // out is a payload that never parses at all — a truncated upload, which is indistinguishable from a real
    // save once written, and which every reader downstream chokes on (settings synchronization parses it).
    private static bool IsWellFormedJson(string content)
    {
        try
        {
            JsonNode.Parse(content);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
