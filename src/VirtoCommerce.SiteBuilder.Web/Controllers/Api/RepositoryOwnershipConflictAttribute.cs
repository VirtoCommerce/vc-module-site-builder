using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using VirtoCommerce.SiteBuilder.Core.GitContent;

namespace VirtoCommerce.SiteBuilder.Web.Controllers.Api;

public sealed class RepositoryOwnershipConflictAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is GitRepositoryOwnershipException conflict)
        {
            context.Result = new ConflictObjectResult(new { error = conflict.Message });
            context.ExceptionHandled = true;
        }
    }
}
