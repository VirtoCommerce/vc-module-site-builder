using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Web.Extensions;

public static class SharedComponentPlacementValidationResultExtensions
{
    public static ActionResult ToActionResult(this SharedComponentPlacementValidationResult validation)
    {
        if (validation.IsForbidden)
        {
            return new ForbidResult();
        }

        return validation.Error != null ? new BadRequestObjectResult(new { error = validation.Error }) : null;
    }
}
