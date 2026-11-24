using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Web.Extensions;

namespace VirtoCommerce.SiteBuilder.Web.Controllers.Api;

[Route("api/site-builder/preview-auth")]
[Authorize]
public class PreviewAuthController(IPreviewAuthCodeService previewAuthCodeService) : Controller
{
    /// <summary>A one-time code the designer's preview exchanges at the storefront's own token endpoint, to render as one of the store's preview users.</summary>
    [HttpPost("code")]
    [ProducesResponseType(typeof(PreviewAuthCode), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PreviewAuthCode>> CreateCode([FromBody] PreviewAuthCodeRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            return BadRequest(new { error = "The request has no body." });
        }

        var operationRequest = new PreviewAuthCodeOperationRequest
        {
            StoreId = request.StoreId,
            UserId = request.UserId,
            BindingErrors = ModelState.GetBindingErrors(),
        };

        return (await previewAuthCodeService.CreateCodeAsync(operationRequest, User, cancellationToken)).ToActionResult();
    }
}
