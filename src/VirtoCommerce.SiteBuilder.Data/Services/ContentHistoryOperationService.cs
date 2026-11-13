using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Data.Services;

[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The services come from dependency injection; a parameter object would only hide them and change the constructor that subclasses call.")]
public class ContentHistoryOperationService(
    IAuthorizationService authorizationService,
    IContentHistory contentHistory,
    IGroupedPageService groupedPageService,
    IDesignerContentService designerContentService,
    IContentPermissionService contentPermissionService,
    ISiteBuilderSharedComponentPlacementValidator sharedComponentPlacementValidator,
    IValidator<SiteBuilderStoreRequest> storeRequestValidator,
    IValidator<ContentHistoryRequest> requestValidator,
    IValidator<ContentHistoryRestoreRequest> restoreRequestValidator) : IContentHistoryOperationService
{
    public virtual Task<SiteBuilderOperationResult<ContentHistoryStatusResult>> GetStatusAsync(SiteBuilderOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetStatusInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<ContentHistoryStatusResult>> GetStatusInternalAsync(SiteBuilderOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var storeRequest = new SiteBuilderStoreRequest { StoreId = request.StoreId };
        if ((await authorizationService.CheckStoreOperationAsync(user, storeRequest)
             ?? await storeRequestValidator.CheckOperationRequestAsync(storeRequest, request.BindingErrors, cancellationToken)) is { } refused)
        {
            return refused.As<ContentHistoryStatusResult>();
        }

        var result = AbstractTypeFactory<ContentHistoryStatusResult>.TryCreateInstance();
        result.Status = await contentHistory.GetStatusAsync(request.StoreId, cancellationToken);

        return SiteBuilderOperationResult.Success(result);
    }

    public virtual Task<SiteBuilderOperationResult<ContentVersionPage>> ListAsync(ContentHistoryListOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ListInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<ContentVersionPage>> ListInternalAsync(ContentHistoryListOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var historyRequest = new ContentHistoryRequest { StoreId = request.StoreId, Items = request.Items ?? [], Cursor = request.Cursor, Take = request.Take };
        if (await CheckRequestAsync(historyRequest, request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<ContentVersionPage>();
        }

        try
        {
            return SiteBuilderOperationResult.Success(await contentHistory.ListAsync(request.StoreId, ParseItems(historyRequest), request.Cursor, request.Take,
                cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message).As<ContentVersionPage>();
        }
    }

    public virtual Task<SiteBuilderOperationResult<string>> ReadVersionAsync(ContentHistoryOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ReadVersionInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<string>> ReadVersionInternalAsync(ContentHistoryOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var historyRequest = new ContentHistoryRequest { StoreId = request.StoreId, Items = request.Items ?? [], VersionId = request.VersionId };
        if (await CheckRequestAsync(historyRequest, request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<string>();
        }

        var content = await contentHistory.ReadAsync(request.StoreId, ParseItems(historyRequest).Single(), request.VersionId, cancellationToken);

        return content == null ? SiteBuilderOperationResult.NotFound().As<string>() : SiteBuilderOperationResult.Success(content);
    }

    public virtual Task<SiteBuilderOperationResult<ContentSaveResult>> RestoreAsync(ContentHistoryRestoreOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RestoreInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<ContentSaveResult>> RestoreInternalAsync(ContentHistoryRestoreOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var (storeId, versionId) = (request.StoreId, request.VersionId);
        var restoreRequest = new ContentHistoryRestoreRequest { StoreId = storeId, Items = request.Items ?? [], VersionId = versionId };
        if ((await authorizationService.CheckStoreOperationAsync(user, restoreRequest)
             ?? await restoreRequestValidator.CheckOperationRequestAsync(restoreRequest, request.BindingErrors, cancellationToken)) is { } refused)
        {
            return refused.As<ContentSaveResult>();
        }

        // the edit permissions first, so a 404 does not reveal which pages exist
        var historyItems = ParseItems(restoreRequest);
        if (!await contentPermissionService.HasPermissionsAsync(user, historyItems.Select(contentPermissionService.GetEditPermission)))
        {
            return SiteBuilderOperationResult.Forbidden().As<ContentSaveResult>();
        }

        var pageItem = historyItems.SingleOrDefault(x => x.Type == HistoryItemType.Page);
        var group = pageItem == null ? null : await groupedPageService.GetByIdAsync(pageItem.Key);
        if (pageItem != null && await CheckPageAccessAsync(storeId, group, user) is { } refusedPage)
        {
            return refusedPage.As<ContentSaveResult>();
        }

        var contents = new Dictionary<HistoryItem, string>();
        foreach (var historyItem in historyItems)
        {
            var content = await contentHistory.ReadAsync(storeId, historyItem, versionId, cancellationToken);
            if (content == null)
            {
                return SiteBuilderOperationResult.NotFound().As<ContentSaveResult>();
            }

            contents[historyItem] = content;
        }

        if (await CheckRestoredPageAsync(storeId, pageItem, contents, user, cancellationToken) is { } refusedContent)
        {
            return refusedContent.As<ContentSaveResult>();
        }

        var command = AbstractTypeFactory<ContentRestoreCommand>.TryCreateInstance();
        command.StoreId = storeId;
        command.VersionId = versionId;
        command.SaveId = request.SaveId;
        command.Group = group;
        command.Contents = contents;
        command.ETags = ParseExpectedVersions(request.ExpectedVersions);
        try
        {
            if (request.DryRun)
            {
                return SiteBuilderOperationResult.DryRun<ContentSaveResult>(await designerContentService.PreviewRestoreAsync(command, cancellationToken));
            }

            return SiteBuilderOperationResult.Success(await designerContentService.RestoreAsync(command, user, cancellationToken));
        }
        catch (SiteBuilderContentConflictException ex)
        {
            return SiteBuilderOperationResult.Conflict(ex.Message).As<ContentSaveResult>();
        }
        catch (KeyNotFoundException)
        {
            return SiteBuilderOperationResult.NotFound().As<ContentSaveResult>();
        }
        catch (InvalidDataException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message).As<ContentSaveResult>();
        }
    }

    protected virtual async Task<SiteBuilderOperationResult> CheckRestoredPageAsync(string storeId, HistoryItem pageItem, IDictionary<HistoryItem, string> contents,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (pageItem == null)
        {
            return null;
        }

        var placement = await sharedComponentPlacementValidator.ValidateAsync(user, storeId, contents[pageItem], cancellationToken);
        if (placement.IsForbidden)
        {
            return SiteBuilderOperationResult.Forbidden();
        }

        return placement.Error != null ? SiteBuilderOperationResult.Invalid(placement.Error) : null;
    }

    /// <summary>Store access first, so a user bound to another store cannot tell from a refused request which stores exist; then access to the pages.</summary>
    protected virtual async Task<SiteBuilderOperationResult> CheckRequestAsync(ContentHistoryRequest request, IList<string> bindingErrors, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        return await authorizationService.CheckStoreOperationAsync(user, request)
               ?? await requestValidator.CheckOperationRequestAsync(request, bindingErrors, cancellationToken)
               ?? await CheckPagesAccessAsync(request.StoreId, ParseItems(request), user);
    }

    protected virtual async Task<SiteBuilderOperationResult> CheckPagesAccessAsync(string storeId, IEnumerable<HistoryItem> items, ClaimsPrincipal user)
    {
        foreach (var item in items.Where(x => x.Type == HistoryItemType.Page))
        {
            if (await CheckPageAccessAsync(storeId, await groupedPageService.GetByIdAsync(item.Key), user) is { } refused)
            {
                return refused;
            }
        }

        return null;
    }

    protected virtual async Task<SiteBuilderOperationResult> CheckPageAccessAsync(string storeId, GroupedSiteBuilderPage group, ClaimsPrincipal user)
    {
        if (group == null)
        {
            return SiteBuilderOperationResult.NotFound();
        }

        return await contentPermissionService.CanAccessPageAsync(user, group, storeId) ? null : SiteBuilderOperationResult.Forbidden();
    }

    /// <summary>An item it cannot read names nothing the restore writes, and is left out.</summary>
    protected virtual IDictionary<HistoryItem, string> ParseExpectedVersions(IDictionary<string, string> expectedVersions)
    {
        if (expectedVersions == null)
        {
            return null;
        }

        var result = new Dictionary<HistoryItem, string>();
        foreach (var (item, version) in expectedVersions)
        {
            if (HistoryItem.TryParse(item, out var historyItem))
            {
                result[historyItem] = version;
            }
        }

        return result;
    }

    protected static IList<HistoryItem> ParseItems(ContentHistoryRequest request) =>
        request.Items.Select(x => HistoryItem.TryParse(x, out var item) ? item : null).Where(x => x != null).ToList();
}
