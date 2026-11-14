using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface ISiteBuilderSharedComponentPlacementValidator
{
    Task<SharedComponentPlacementValidationResult> ValidateAsync(ClaimsPrincipal user, string storeId, string content,
        CancellationToken cancellationToken = default);
}
