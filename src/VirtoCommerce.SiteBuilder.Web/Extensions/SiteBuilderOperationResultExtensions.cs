using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Web.Extensions;

public static class SiteBuilderOperationResultExtensions
{
    public static ActionResult ToActionResult<T>(this SiteBuilderOperationResult<T> result) =>
        result.Succeeded ? new OkObjectResult(result.Value) : result.ToRefusal();

    /// <summary>Returns <c>null</c> for an operation that succeeded.</summary>
    public static ActionResult ToRefusal(this SiteBuilderOperationResult result) => result.Status switch
    {
        SiteBuilderOperationStatus.Succeeded => null,
        SiteBuilderOperationStatus.Forbidden => new ForbidResult(),
        SiteBuilderOperationStatus.NotFound => new NotFoundResult(),
        SiteBuilderOperationStatus.Invalid => new BadRequestObjectResult(result.Errors == null
            ? (object)new { error = result.Error }
            : new { error = result.Error, errors = result.Errors }),
        SiteBuilderOperationStatus.Conflict => new ObjectResult(result.Error) { StatusCode = StatusCodes.Status412PreconditionFailed },
        SiteBuilderOperationStatus.VersionRequired => new ObjectResult(result.Error) { StatusCode = StatusCodes.Status428PreconditionRequired },
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.Status, null),
    };
}
