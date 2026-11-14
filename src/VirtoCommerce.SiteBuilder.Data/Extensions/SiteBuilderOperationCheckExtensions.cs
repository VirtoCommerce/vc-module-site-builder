using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Authorization;

namespace VirtoCommerce.SiteBuilder.Data.Extensions;

public static class SiteBuilderOperationCheckExtensions
{
    /// <summary>Returns <c>null</c> when the user holds the permission in the store.</summary>
    public static async Task<SiteBuilderOperationResult> CheckStoreOperationAsync(this IAuthorizationService authorizationService, ClaimsPrincipal user,
        object resource, string permission = ModuleConstants.Security.Permissions.Read)
    {
        var authorizationResult = await authorizationService.AuthorizeAsync(user, resource, new SiteBuilderAuthorizationRequirement(permission));
        return authorizationResult.Succeeded ? null : SiteBuilderOperationResult.Forbidden();
    }

    /// <summary>Returns <c>null</c> for a valid request.</summary>
    public static async Task<SiteBuilderOperationResult> CheckOperationRequestAsync<T>(this IValidator<T> validator, T request,
        CancellationToken cancellationToken)
    {
        var validationResult = await validator.ValidateAsync(request, cancellationToken);

        return validationResult.IsValid
            ? null
            : SiteBuilderOperationResult.Invalid(string.Join(" ", validationResult.Errors.Select(x => x.ErrorMessage)));
    }

    /// <summary>Treats what the caller sent that could not be read as its mistake too, where the transport would pass a default on.</summary>
    public static Task<SiteBuilderOperationResult> CheckOperationRequestAsync<T>(this IValidator<T> validator, T request, IList<string> bindingErrors,
        CancellationToken cancellationToken)
    {
        return bindingErrors?.Count > 0
            ? Task.FromResult(SiteBuilderOperationResult.Invalid(string.Join(" ", bindingErrors)))
            : validator.CheckOperationRequestAsync(request, cancellationToken);
    }
}
