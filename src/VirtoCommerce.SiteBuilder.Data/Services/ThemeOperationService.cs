using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.Platform.Core.Common;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.ChangePreview;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public class ThemeOperationService(
    IAuthorizationService authorizationService,
    IValidator<SiteBuilderStoreRequest> storeRequestValidator,
    IThemeSnapshotService themeSnapshotService,
    IThemePublisher themePublisher,
    IContentHistory contentHistory) : IThemeOperationService
{
    private const string NotNewSiteBuilderError = "The store does not use the new Site Builder: its theme is not published with theme/publish.";
    private const string NoHistoryWarning = "The store keeps no content history, so who last changed the drafts is not known.";

    public virtual Task<SiteBuilderOperationResult<ThemePublicationPointer>> PublishAsync(ThemePublishOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PublishInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<ThemePublicationPointer>> PublishInternalAsync(ThemePublishOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var storeRequest = new SiteBuilderStoreRequest { StoreId = request.StoreId, CatalogHash = request.CatalogHash };
        if (await CheckRequestAsync(storeRequest, request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<ThemePublicationPointer>();
        }

        if (!await themeSnapshotService.IsNewSiteBuilderEnabledAsync(request.StoreId, cancellationToken))
        {
            return SiteBuilderOperationResult.Invalid(NotNewSiteBuilderError).As<ThemePublicationPointer>();
        }

        try
        {
            if (request.DryRun)
            {
                var publication = await themePublisher.PreviewPublishAsync(request.StoreId, request.CatalogHash, cancellationToken);
                return SiteBuilderOperationResult.DryRun<ThemePublicationPointer>(await CreatePublishPreviewAsync(request.StoreId, publication, cancellationToken));
            }

            var result = AbstractTypeFactory<ThemePublicationPointer>.TryCreateInstance();
            result.Publication = await themePublisher.PublishAsync(request.StoreId, request.CatalogHash, request.ExpectedVersions, user, cancellationToken);

            return SiteBuilderOperationResult.Success(result, affectsPublished: true);
        }
        catch (ThemeValidationException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message, ex.Errors).As<ThemePublicationPointer>();
        }
        catch (SiteBuilderContentConflictException ex)
        {
            return SiteBuilderOperationResult.Conflict(ex.Message).As<ThemePublicationPointer>();
        }
    }

    public virtual Task<SiteBuilderOperationResult<ThemeStatus>> GetStatusAsync(SiteBuilderOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetStatusInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<ThemeStatus>> GetStatusInternalAsync(SiteBuilderOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var storeRequest = new SiteBuilderStoreRequest { StoreId = request.StoreId };
        if (await CheckRequestAsync(storeRequest, request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<ThemeStatus>();
        }

        return SiteBuilderOperationResult.Success(await themePublisher.GetStatusAsync(request.StoreId, cancellationToken));
    }

    /// <summary>Store access first, so a user bound to another store cannot tell from a refused request which stores exist.</summary>
    protected virtual async Task<SiteBuilderOperationResult> CheckRequestAsync(SiteBuilderStoreRequest request, IList<string> bindingErrors,
        ClaimsPrincipal user, CancellationToken cancellationToken) =>
        await authorizationService.CheckStoreOperationAsync(user, request)
        ?? await storeRequestValidator.CheckOperationRequestAsync(request, bindingErrors, cancellationToken);

    /// <summary>Every draft goes live, at the version it has now; the publication itself reaches what the storefront serves even with none.</summary>
    protected virtual async Task<SiteBuilderChangePreview> CreatePublishPreviewAsync(string storeId, ThemePublicationPreview publication,
        CancellationToken cancellationToken)
    {
        var preview = AbstractTypeFactory<SiteBuilderChangePreview>.TryCreateInstance();
        preview.AffectsPublished = true;

        var historyKept = true;
        foreach (var draft in publication.Drafts)
        {
            var change = preview.AddObject(ThemeFile, draft.Path, draft.ETag).AddField(Status, Draft, Published);
            if (historyKept)
            {
                historyKept = await AddAuthorAsync(storeId, change, preview, cancellationToken);
            }
        }

        if (!historyKept)
        {
            preview.Warnings.Add(NoHistoryWarning);
        }

        foreach (var warning in publication.Warnings)
        {
            preview.Warnings.Add(warning);
        }

        return preview;
    }

    /// <summary>
    /// The author of a draft is that of the last version of the file in the history, when that version is a save: a draft holds what was saved
    /// last, while a published version means the draft was written without the history hearing of it.
    /// </summary>
    /// <returns><c>false</c> when the store keeps no history.</returns>
    protected virtual async Task<bool> AddAuthorAsync(string storeId, SiteBuilderObjectChange change, SiteBuilderChangePreview preview,
        CancellationToken cancellationToken)
    {
        var versions = await contentHistory.ListAsync(storeId, [HistoryItem.ForThemeFile(change.ObjectId)], cursor: null, take: 1, cancellationToken);
        if (versions.Status != ContentHistoryStatus.Enabled)
        {
            return false;
        }

        if (versions.Versions.FirstOrDefault() is { Published: false, Author: { } author } version)
        {
            change.ChangedBy = author;
            change.ChangedDate = version.Date;
        }
        else
        {
            preview.Warnings.Add($"The history records no author of the draft of {change.ObjectId}.");
        }

        return true;
    }
}
