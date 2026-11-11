using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.StoreModule.Core.Model;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public class SiteBuilderSharedComponentPlacementValidator(
    ISiteBuilderSharedComponentReferenceIndexService referenceIndexService,
    IAuthorizationService authorizationService)
    : ISiteBuilderSharedComponentPlacementValidator
{
    public virtual async Task<SharedComponentPlacementValidationResult> ValidateAsync(ClaimsPrincipal user, string storeId, string content,
        CancellationToken cancellationToken = default)
    {
        var result = AbstractTypeFactory<SharedComponentPlacementValidationResult>.TryCreateInstance();

        try
        {
            // a user who may not read shared components learns nothing about the store's ones
            if (SiteBuilderSharedComponentReferenceMatcher.HasReferences(content) && !await CanReadSharedComponentsAsync(user, storeId))
            {
                result.IsForbidden = true;
                return result;
            }

            await referenceIndexService.ValidateReferencesForStoreAsync(storeId, [content], cancellationToken);
        }
        catch (InvalidDataException ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    protected virtual async Task<bool> CanReadSharedComponentsAsync(ClaimsPrincipal user, string storeId)
    {
        var store = AbstractTypeFactory<Store>.TryCreateInstance();
        store.Id = storeId;

        var authorizationResult = await authorizationService.AuthorizeAsync(user, store,
            new SiteBuilderAuthorizationRequirement(ModuleConstants.Security.Permissions.SharedComponentsRead));
        return authorizationResult.Succeeded;
    }
}
