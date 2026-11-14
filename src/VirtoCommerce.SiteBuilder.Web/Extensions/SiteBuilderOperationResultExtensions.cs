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
        SiteBuilderOperationStatus.ConcurrentChange => new ConflictObjectResult(new { error = result.Error }),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.Status, null),
    };

    /// <inheritdoc cref="ToPageRefusal"/>
    public static ActionResult ToPageActionResult<T>(this SiteBuilderOperationResult<T> result) =>
        result.Succeeded ? new OkObjectResult(result.Value) : result.ToPageRefusal();

    /// <summary>
    /// The refusal as the page and multilanguage page endpoints answer it: an empty object for a forbidden request, the error as plain text.
    /// Returns <c>null</c> for an operation that succeeded.
    /// </summary>
    public static ActionResult ToPageRefusal(this SiteBuilderOperationResult result) => result.Status switch
    {
        SiteBuilderOperationStatus.Succeeded => null,
        SiteBuilderOperationStatus.Forbidden => new ObjectResult(new { }) { StatusCode = StatusCodes.Status403Forbidden },
        SiteBuilderOperationStatus.NotFound => new NotFoundResult(),
        SiteBuilderOperationStatus.Invalid => new BadRequestObjectResult(result.Error),
        SiteBuilderOperationStatus.Conflict => new ObjectResult(result.Error) { StatusCode = StatusCodes.Status412PreconditionFailed },
        SiteBuilderOperationStatus.VersionRequired => new ObjectResult(result.Error) { StatusCode = StatusCodes.Status428PreconditionRequired },
        SiteBuilderOperationStatus.ConcurrentChange => new ConflictObjectResult(result.Error),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.Status, null),
    };
}
