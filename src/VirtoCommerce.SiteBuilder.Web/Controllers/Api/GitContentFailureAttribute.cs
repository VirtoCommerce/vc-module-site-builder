using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using VirtoCommerce.SiteBuilder.Core.GitContent;

namespace VirtoCommerce.SiteBuilder.Web.Controllers.Api;

public sealed class GitContentFailureAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is GitContentException failure)
        {
            context.Result = new ObjectResult(new { error = failure.Message }) { StatusCode = StatusCodes.Status502BadGateway };
            context.ExceptionHandled = true;
        }
    }
}
