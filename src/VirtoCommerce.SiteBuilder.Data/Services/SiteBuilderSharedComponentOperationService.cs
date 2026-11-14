using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.StoreModule.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.ChangePreview;

namespace VirtoCommerce.SiteBuilder.Data.Services;

[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The services come from dependency injection; a parameter object would only hide them and change the constructor that subclasses call.")]
public class SiteBuilderSharedComponentOperationService(
    ISiteBuilderSharedComponentService sharedComponentService,
    ISiteBuilderSharedComponentSearchService sharedComponentSearchService,
    ISiteBuilderSharedComponentContentService sharedComponentContentService,
    ISiteBuilderSharedComponentUsageService sharedComponentUsageService,
    IAuthorizationService authorizationService,
    IStoreService storeService,
    ISiteBuilderContentGuard contentGuard) : ISiteBuilderSharedComponentOperationService
{
    private const string ComponentName = "shared component";
    private const string StoreIdRequiredError = "StoreId is required.";
    private const string CreateFieldsRequiredError = "StoreId, Name, and Content are required.";
    private const string NameRequiredError = "Name is required.";
    private const string ContentRequiredError = "Content is required.";
    private const string StoreMoveError = "A Shared Component cannot be moved to another store.";
    private const int NameMaxLength = ModuleConstants.SharedComponents.NameMaxLength;

    public virtual Task<SiteBuilderOperationResult<SiteBuilderSharedComponentSearchResult>> SearchAsync(SiteBuilderSharedComponentSearchCriteria criteria,
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        return SearchInternalAsync(criteria, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<SiteBuilderSharedComponentSearchResult>> SearchInternalAsync(SiteBuilderSharedComponentSearchCriteria criteria,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (criteria == null || string.IsNullOrWhiteSpace(criteria.StoreId))
        {
            return SiteBuilderOperationResult.Invalid(StoreIdRequiredError).As<SiteBuilderSharedComponentSearchResult>();
        }

        if (await CheckAccessAsync(criteria, user) is { } forbidden)
        {
            return forbidden.As<SiteBuilderSharedComponentSearchResult>();
        }

        var result = await sharedComponentSearchService.SearchAsync(criteria);
        await sharedComponentUsageService.ApplyUsageAsync(result.Results, includePages: false, cancellationToken);

        return SiteBuilderOperationResult.Success(result);
    }

    public virtual Task<SiteBuilderOperationResult<SiteBuilderSharedComponent>> GetAsync(SharedComponentOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<SiteBuilderSharedComponent>> GetInternalAsync(SharedComponentOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var (component, refused) = await GetComponentAsync(request.Id, user);
        if (refused != null)
        {
            return refused.As<SiteBuilderSharedComponent>();
        }

        await ApplyUsageAsync(component, user, cancellationToken);

        return SiteBuilderOperationResult.Success(component);
    }

    public virtual Task<SiteBuilderOperationResult<SiteBuilderSharedComponent>> CreateAsync(SharedComponentCreateOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return CreateInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<SiteBuilderSharedComponent>> CreateInternalAsync(SharedComponentCreateOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.StoreId) || string.IsNullOrWhiteSpace(request.Name) || request.Content == null)
        {
            return SiteBuilderOperationResult.Invalid(CreateFieldsRequiredError).As<SiteBuilderSharedComponent>();
        }

        if (CheckNameLength(request.Name) is { } tooLong)
        {
            return tooLong.As<SiteBuilderSharedComponent>();
        }

        var component = AbstractTypeFactory<SiteBuilderSharedComponent>.TryCreateInstance();
        component.StoreId = request.StoreId;
        component.Name = request.Name;

        if (await CheckAccessAsync(component, user) is { } forbidden)
        {
            return forbidden.As<SiteBuilderSharedComponent>();
        }

        if (await storeService.GetNoCloneAsync(request.StoreId) == null)
        {
            return SiteBuilderOperationResult.Invalid($"Store '{request.StoreId}' does not exist.").As<SiteBuilderSharedComponent>();
        }

        try
        {
            var content = GuardContent(request.Content);
            if (request.DryRun)
            {
                var preview = CreatePreview();
                preview.AddObject(SharedComponent, objectId: null)
                    .AddField(StoreId, null, component.StoreId)
                    .AddField(Name, null, component.Name)
                    .AddField(Content, null, content);
                return SiteBuilderOperationResult.DryRun<SiteBuilderSharedComponent>(preview);
            }

            await sharedComponentService.SaveWithContentAsync(component, content, cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message).As<SiteBuilderSharedComponent>();
        }

        return SiteBuilderOperationResult.Success(component);
    }

    public virtual Task<SiteBuilderOperationResult<SiteBuilderSharedComponent>> UpdateAsync(SharedComponentUpdateOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UpdateInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<SiteBuilderSharedComponent>> UpdateInternalAsync(SharedComponentUpdateOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var (component, refused) = await GetComponentAsync(request.Id, user);
        if (refused != null)
        {
            return refused.As<SiteBuilderSharedComponent>();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return SiteBuilderOperationResult.Invalid(NameRequiredError).As<SiteBuilderSharedComponent>();
        }

        if (CheckNameLength(request.Name) is { } tooLong)
        {
            return tooLong.As<SiteBuilderSharedComponent>();
        }

        if (!string.IsNullOrWhiteSpace(request.StoreId) &&
            !string.Equals(request.StoreId, component.StoreId, StringComparison.OrdinalIgnoreCase))
        {
            return SiteBuilderOperationResult.Invalid(StoreMoveError).As<SiteBuilderSharedComponent>();
        }

        if (request.DryRun)
        {
            var preview = CreatePreview();
            preview.AddObject(SharedComponent, component.Id).AddField(Name, component.Name, request.Name);
            return SiteBuilderOperationResult.DryRun<SiteBuilderSharedComponent>(preview);
        }

        component.Name = request.Name;
        component = await sharedComponentService.UpdateMetadataAsync(component, cancellationToken);
        if (component == null)
        {
            // The component may have been deleted after the authorization read. The metadata service reloads
            // it under the component row lock and never turns that race into an upsert.
            return SiteBuilderOperationResult.NotFound().As<SiteBuilderSharedComponent>();
        }

        await ApplyUsageAsync(component, user, cancellationToken);

        return SiteBuilderOperationResult.Success(component);
    }

    public virtual Task<SiteBuilderOperationResult> SaveContentAsync(SharedComponentContentSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SaveContentInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult> SaveContentInternalAsync(SharedComponentContentSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var (component, refused) = await GetComponentAsync(request.Id, user);
        if (refused != null)
        {
            return refused;
        }

        if (request.Content == null)
        {
            return SiteBuilderOperationResult.Invalid(ContentRequiredError);
        }

        try
        {
            var content = GuardContent(request.Content);
            if (request.DryRun)
            {
                var usage = await GetUsageAsync(component, cancellationToken);
                return SiteBuilderOperationResult.DryRun(await CreateContentPreviewAsync(component, content, usage, usage.Pages.Any(IsPublished), user,
                    cancellationToken));
            }

            // the save does not need the pages the component is placed on: they are read only for a caller that asks how far it reaches
            var affectsPublished = request.ReportImpact && (await GetUsageAsync(component, cancellationToken)).Pages.Any(IsPublished);

            if (!await sharedComponentContentService.TrySaveContentAsync(component, content, cancellationToken))
            {
                return SiteBuilderOperationResult.NotFound();
            }

            return SiteBuilderOperationResult.Success(affectsPublished);
        }
        catch (InvalidDataException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message);
        }
    }

    /// <summary>
    /// The content and the pages it is placed on, so that whoever approves the write sees its reach. The pages are named to a user who may
    /// read them; to another, only told by their number.
    /// </summary>
    protected virtual async Task<SiteBuilderChangePreview> CreateContentPreviewAsync(SiteBuilderSharedComponent component, string content,
        SiteBuilderSharedComponentUsage usage, bool affectsPublished, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var current = await sharedComponentContentService.TryLoadContentAsync(component, cancellationToken);

        var preview = CreatePreview();
        preview.AffectsPublished = affectsPublished;
        preview.AddObject(SharedComponent, component.Id).AddField(Content, current, content);
        if (await CanReadPagesAsync(component, user))
        {
            foreach (var page in usage.Pages)
            {
                preview.AddObject(Page, page.Id);
            }
        }
        else if (usage.UsageCount > 0)
        {
            preview.Warnings.Add($"The component is placed on {usage.UsageCount} pages of the store, which you may not read.");
        }

        if (affectsPublished)
        {
            preview.Warnings.Add($"{usage.Pages.Count(IsPublished)} of the pages it is placed on are published, and show the new content as soon as it is saved.");
        }

        return preview;
    }

    /// <summary>Every page the component is placed on, whoever asks: whether a published one shows it decides how far the write reaches.</summary>
    protected virtual async Task<SiteBuilderSharedComponentUsage> GetUsageAsync(SiteBuilderSharedComponent component, CancellationToken cancellationToken)
    {
        var usages = await sharedComponentUsageService.GetUsageAsync([component.Id], component.StoreId, includePages: true, cancellationToken);

        return usages.FirstOrDefault(x => string.Equals(x.SharedComponentId, component.Id, StringComparison.OrdinalIgnoreCase))
            ?? AbstractTypeFactory<SiteBuilderSharedComponentUsage>.TryCreateInstance();
    }

    /// <summary>A page's status lists the statuses of its versions placing the component, such as "Draft, Published".</summary>
    protected virtual bool IsPublished(SiteBuilderSharedComponentUsagePage page) =>
        page.Status?.Split(',', StringSplitOptions.TrimEntries).Contains(ModuleConstants.PageStatuses.Published, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>The component is looked up first, then access to it is checked.</summary>
    protected virtual async Task<(SiteBuilderSharedComponent Component, SiteBuilderOperationResult Refused)> GetComponentAsync(string id, ClaimsPrincipal user)
    {
        var component = await sharedComponentService.GetByIdAsync(id);
        if (component == null)
        {
            return (null, SiteBuilderOperationResult.NotFound());
        }

        return await CheckAccessAsync(component, user) is { } forbidden ? (null, forbidden) : (component, null);
    }

    protected virtual async Task ApplyUsageAsync(SiteBuilderSharedComponent component, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        await sharedComponentUsageService.ApplyUsageAsync([component], includePages: await CanReadPagesAsync(component, user), cancellationToken);

    protected virtual Task<SiteBuilderOperationResult> CheckAccessAsync(object resource, ClaimsPrincipal user) =>
        authorizationService.CheckStoreOperationAsync(user, resource, ModuleConstants.Security.Permissions.SharedComponentsRead);

    protected virtual async Task<bool> CanReadPagesAsync(object resource, ClaimsPrincipal user)
    {
        var result = await authorizationService.AuthorizeAsync(user, resource, new SiteBuilderAuthorizationRequirement());
        return result.Succeeded;
    }

    protected virtual SiteBuilderOperationResult CheckNameLength(string name) =>
        name.Trim().Length > NameMaxLength ? SiteBuilderOperationResult.Invalid($"Name cannot exceed {NameMaxLength} characters.") : null;

    /// <exception cref="InvalidDataException">Something in the content can run script.</exception>
    protected virtual string GuardContent(JObject content)
    {
        var errors = contentGuard.GuardPage(content, ComponentName, catalog: null).Errors;
        SiteBuilderContentGuardExtensions.ThrowIfAny(errors);

        return content.ToString(Formatting.None);
    }

    protected virtual SiteBuilderChangePreview CreatePreview() => AbstractTypeFactory<SiteBuilderChangePreview>.TryCreateInstance();
}
