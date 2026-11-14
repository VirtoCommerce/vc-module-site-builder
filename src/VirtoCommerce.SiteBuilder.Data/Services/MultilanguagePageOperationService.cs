using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.StoreModule.Core.Services;

namespace VirtoCommerce.SiteBuilder.Data.Services;

/// <summary>
/// The checks of the request and of the user, around the writes of <see cref="IMultilanguagePageService"/>. A dry run answers the write's preview,
/// which makes the same checks as the write; a write runs its preview first only for a caller that asks whether it reaches what the storefront serves.
/// </summary>
public class MultilanguagePageOperationService(
    IAuthorizationService authorizationService,
    IMultilanguagePageService multilanguagePageService,
    IGroupedPageService groupedPageService,
    IStoreService storeService) : IMultilanguagePageOperationService
{
    private const string ConcurrentChangeMessage = "The multilanguage page was changed by another request. Reload it and try again.";
    private const string CreateFieldsRequiredError = "StoreId, Name and Permalink are required.";
    private const string UpdateFieldsRequiredError = "Id, Name and Permalink are required.";
    private const string StoreChangeError = "The store of a multilanguage page cannot be changed.";

    public virtual Task<SiteBuilderOperationResult<MultilanguagePage>> CreateAsync(MultilanguagePageSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return CreateInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<MultilanguagePage>> CreateInternalAsync(MultilanguagePageSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var model = request.Page;
        if (model == null ||
            string.IsNullOrWhiteSpace(model.StoreId) ||
            string.IsNullOrWhiteSpace(model.Name) ||
            string.IsNullOrWhiteSpace(model.Permalink))
        {
            return SiteBuilderOperationResult.Invalid(CreateFieldsRequiredError).As<MultilanguagePage>();
        }

        if ((CheckRoot(model, existing: null) ?? await authorizationService.CheckStoreOperationAsync(user, model)) is { } refused)
        {
            return refused.As<MultilanguagePage>();
        }

        if (await storeService.GetNoCloneAsync(model.StoreId) == null)
        {
            return SiteBuilderOperationResult.Invalid($"Store '{model.StoreId}' does not exist.").As<MultilanguagePage>();
        }

        try
        {
            return await RunAsync(request.DryRun, request.ReportImpact,
                () => multilanguagePageService.PreviewCreateAsync(model, cancellationToken),
                () => multilanguagePageService.CreateAsync(model, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message).As<MultilanguagePage>();
        }
    }

    public virtual Task<SiteBuilderOperationResult<MultilanguagePage>> UpdateAsync(MultilanguagePageSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UpdateInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<MultilanguagePage>> UpdateInternalAsync(MultilanguagePageSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var model = request.Page;
        if (model == null ||
            string.IsNullOrWhiteSpace(model.Id) ||
            string.IsNullOrWhiteSpace(model.Name) ||
            string.IsNullOrWhiteSpace(model.Permalink))
        {
            return SiteBuilderOperationResult.Invalid(UpdateFieldsRequiredError).As<MultilanguagePage>();
        }

        var existing = await multilanguagePageService.GetByIdAsync(model.Id);
        if (existing == null)
        {
            return SiteBuilderOperationResult.NotFound().As<MultilanguagePage>();
        }

        if ((await authorizationService.CheckStoreOperationAsync(user, existing) ?? CheckRoot(model, existing)) is { } refused)
        {
            return refused.As<MultilanguagePage>();
        }

        try
        {
            return await RunAsync(request.DryRun, request.ReportImpact,
                () => multilanguagePageService.PreviewUpdateAsync(model, cancellationToken),
                () => request.RequireUnpublished || request.ExpectedSettingsVersion != null
                    ? multilanguagePageService.UpdateAsync(model, request.RequireUnpublished, request.ExpectedSettingsVersion, cancellationToken)
                    : multilanguagePageService.UpdateAsync(model, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message).As<MultilanguagePage>();
        }
        catch (SiteBuilderContentConflictException ex)
        {
            return SiteBuilderOperationResult.Conflict(ex.Message).As<MultilanguagePage>();
        }
    }

    public virtual Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> CreateLanguageAsync(MultilanguagePageLanguageOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RunForRootAsync(request, user,
            () => multilanguagePageService.PreviewCreateLanguageAsync(request.MultilanguagePageId, request.CultureName, request.CopyContent, cancellationToken),
            () => multilanguagePageService.CreateLanguageAsync(request.MultilanguagePageId, request.CultureName, request.CopyContent, cancellationToken));
    }

    public virtual Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> LinkAsync(MultilanguagePageLinkOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return LinkInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> LinkInternalAsync(MultilanguagePageLinkOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await CheckGroupAsync(request.GroupId, user) is (_, { } refused))
        {
            return refused.As<GroupedSiteBuilderPage>();
        }

        return await RunForRootAsync(request, user,
            () => multilanguagePageService.PreviewLinkAsync(request.MultilanguagePageId, request.GroupId, cancellationToken),
            () => request.RequireUnpublished
                ? multilanguagePageService.LinkAsync(request.MultilanguagePageId, request.GroupId, requireUnpublished: true, cancellationToken)
                : multilanguagePageService.LinkAsync(request.MultilanguagePageId, request.GroupId, cancellationToken));
    }

    public virtual Task<SiteBuilderOperationResult<MultilanguagePage>> ConvertAsync(MultilanguagePageVersionOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ConvertInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<MultilanguagePage>> ConvertInternalAsync(MultilanguagePageVersionOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (await CheckGroupAsync(request.GroupId, user) is (_, { } refused))
        {
            return refused.As<MultilanguagePage>();
        }

        try
        {
            return await RunAsync(request.DryRun, request.ReportImpact,
                () => multilanguagePageService.PreviewConvertAsync(request.GroupId, cancellationToken),
                () => request.RequireUnpublished
                    ? multilanguagePageService.ConvertAsync(request.GroupId, requireUnpublished: true, cancellationToken)
                    : multilanguagePageService.ConvertAsync(request.GroupId, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message).As<MultilanguagePage>();
        }
        catch (SiteBuilderContentConflictException ex)
        {
            return SiteBuilderOperationResult.Conflict(ex.Message).As<MultilanguagePage>();
        }
        catch (DbUpdateException)
        {
            // The page was linked or converted by a concurrent request (unique index).
            return SiteBuilderOperationResult.ConcurrentChange(ConcurrentChangeMessage).As<MultilanguagePage>();
        }
    }

    public virtual Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> MarkUpToDateAsync(MultilanguagePageMarkUpToDateOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return MarkUpToDateInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> MarkUpToDateInternalAsync(MultilanguagePageMarkUpToDateOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var (group, refused) = await CheckGroupAsync(request.GroupId, user);
        if (refused != null)
        {
            return refused.As<GroupedSiteBuilderPage>();
        }

        if (string.IsNullOrEmpty(group.MultilanguagePageId))
        {
            return SiteBuilderOperationResult.Invalid($"Page '{request.GroupId}' is not a language version.").As<GroupedSiteBuilderPage>();
        }

        try
        {
            return await RunAsync(request.DryRun, request.ReportImpact,
                () => multilanguagePageService.PreviewMarkUpToDateAsync(request.GroupId, cancellationToken),
                () => request.ExpectedSourceRevisionDate == null
                    ? multilanguagePageService.MarkUpToDateAsync(request.GroupId, cancellationToken)
                    : multilanguagePageService.MarkUpToDateAsync(request.GroupId, request.ExpectedSourceRevisionDate, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message).As<GroupedSiteBuilderPage>();
        }
        catch (SiteBuilderContentConflictException ex)
        {
            return SiteBuilderOperationResult.Conflict(ex.Message).As<GroupedSiteBuilderPage>();
        }
    }

    public virtual Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> UnlinkAsync(MultilanguagePageVersionOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UnlinkInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> UnlinkInternalAsync(MultilanguagePageVersionOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (await CheckGroupAsync(request.GroupId, user) is (_, { } refused))
        {
            return refused.As<GroupedSiteBuilderPage>();
        }

        if (!request.RequireUnpublished)
        {
            return await RunAsync(request.DryRun, request.ReportImpact,
                () => multilanguagePageService.PreviewUnlinkAsync(request.GroupId, cancellationToken),
                () => multilanguagePageService.UnlinkAsync(request.GroupId, cancellationToken));
        }

        try
        {
            return await RunAsync(request.DryRun, request.ReportImpact,
                () => multilanguagePageService.PreviewUnlinkAsync(request.GroupId, cancellationToken),
                () => multilanguagePageService.UnlinkAsync(request.GroupId, requireUnpublished: true, cancellationToken));
        }
        catch (SiteBuilderContentConflictException ex)
        {
            return SiteBuilderOperationResult.Conflict(ex.Message).As<GroupedSiteBuilderPage>();
        }
    }

    public virtual Task<SiteBuilderOperationResult<IList<string>>> ResetOverridesAsync(MultilanguagePageResetOverridesOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RunForRootAsync(request, user,
            () => multilanguagePageService.PreviewResetOverridesAsync(request.MultilanguagePageId, request.Fields, cancellationToken),
            () => request.RequireUnpublished
                ? multilanguagePageService.ResetOverridesAsync(request.MultilanguagePageId, request.Fields, requireUnpublished: true, cancellationToken)
                : multilanguagePageService.ResetOverridesAsync(request.MultilanguagePageId, request.Fields, cancellationToken));
    }

    public virtual Task<SiteBuilderOperationResult<MultilanguagePage>> CloneAsync(MultilanguagePageOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RunForRootAsync(request, user,
            () => multilanguagePageService.PreviewCloneAsync(request.MultilanguagePageId, cancellationToken),
            () => multilanguagePageService.CloneAsync(request.MultilanguagePageId, cancellationToken));
    }

    public virtual Task<SiteBuilderOperationResult<MultilanguagePageBatchResult>> PublishAllAsync(MultilanguagePagePublishAllOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RunForRootAsync(request, user,
            () => multilanguagePageService.PreviewPublishAllAsync(request.MultilanguagePageId, cancellationToken),
            () => request.ExpectedDraftVersions == null
                ? multilanguagePageService.PublishAllAsync(request.MultilanguagePageId, cancellationToken)
                : multilanguagePageService.PublishAllAsync(request.MultilanguagePageId, request.ExpectedDraftVersions, cancellationToken));
    }

    /// <summary>The root must exist and be in a store of the user; the write's expected failures become refusals.</summary>
    protected virtual async Task<SiteBuilderOperationResult<T>> RunForRootAsync<T>(MultilanguagePageOperationRequest request, ClaimsPrincipal user,
        Func<Task<SiteBuilderChangePreview>> preview, Func<Task<T>> write)
    {
        var root = await multilanguagePageService.GetNoCloneAsync(request.MultilanguagePageId);
        if (root == null)
        {
            return SiteBuilderOperationResult.NotFound().As<T>();
        }

        if (await authorizationService.CheckStoreOperationAsync(user, root) is { } forbidden)
        {
            return forbidden.As<T>();
        }

        try
        {
            return await RunAsync(request.DryRun, request.ReportImpact, preview, write);
        }
        catch (KeyNotFoundException)
        {
            return SiteBuilderOperationResult.NotFound().As<T>();
        }
        catch (SiteBuilderContentConflictException ex)
        {
            return SiteBuilderOperationResult.Conflict(ex.Message).As<T>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message).As<T>();
        }
        catch (DbUpdateException)
        {
            // A concurrent request took the language (unique index) or removed the page (foreign key); the data is intact.
            return SiteBuilderOperationResult.ConcurrentChange(ConcurrentChangeMessage).As<T>();
        }
    }

    /// <summary>The write makes its own checks, so it runs its preview only to tell whether it reaches the storefront, when asked to.</summary>
    protected virtual async Task<SiteBuilderOperationResult<T>> RunAsync<T>(bool dryRun, bool reportImpact, Func<Task<SiteBuilderChangePreview>> preview,
        Func<Task<T>> write)
    {
        if (dryRun)
        {
            return SiteBuilderOperationResult.DryRun<T>(await preview());
        }

        var affectsPublished = reportImpact && (await preview()).AffectsPublished;

        return SiteBuilderOperationResult.Success(await write(), affectsPublished);
    }

    /// <returns>The page, and the refusal when it does not exist or the user may not reach its store.</returns>
    protected virtual async Task<(GroupedSiteBuilderPage Group, SiteBuilderOperationResult Refused)> CheckGroupAsync(string groupId, ClaimsPrincipal user)
    {
        var group = await groupedPageService.GetNoCloneAsync(groupId);
        if (group == null)
        {
            return (null, SiteBuilderOperationResult.NotFound());
        }

        return (group, await authorizationService.CheckStoreOperationAsync(user, group));
    }

    private static SiteBuilderOperationResult CheckRoot(MultilanguagePage model, MultilanguagePage existing)
    {
        if (!SiteBuilderPageName.IsValid(model.Name))
        {
            return SiteBuilderOperationResult.Invalid(SiteBuilderPageName.InvalidMessage);
        }

        // An unchanged permalink stored before validation existed must not block editing the root's other fields.
        if (existing?.Permalink != model.Permalink && !SiteBuilderPermalink.IsValid(model.Permalink))
        {
            return SiteBuilderOperationResult.Invalid(SiteBuilderPermalink.InvalidMessage);
        }

        // A multilanguage page never moves to another store; a client asking for it is told so instead of being ignored.
        if (existing != null && !string.IsNullOrEmpty(model.StoreId) &&
            !string.Equals(model.StoreId, existing.StoreId, StringComparison.OrdinalIgnoreCase))
        {
            return SiteBuilderOperationResult.Invalid(StoreChangeError);
        }

        return null;
    }
}
