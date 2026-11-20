using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Web.Extensions;

namespace VirtoCommerce.SiteBuilder.Web.Controllers.Api;

[Route("api/site-builder/history")]
[Authorize]
[RepositoryOwnershipConflict]
[GitContentFailure]
public class HistoryController(IContentHistoryOperationService contentHistoryOperationService) : Controller
{
    [HttpGet("status")]
    public async Task<ActionResult<ContentHistoryStatusResult>> GetStatus([FromQuery] string storeId, CancellationToken cancellationToken = default)
    {
        var request = new SiteBuilderOperationRequest { StoreId = storeId, BindingErrors = ModelState.GetBindingErrors() };

        return (await contentHistoryOperationService.GetStatusAsync(request, User, cancellationToken)).ToActionResult();
    }

    [HttpGet]
    public async Task<ActionResult<ContentVersionPage>> GetHistory([FromQuery] string storeId, [FromQuery(Name = "item")] string[] items,
        [FromQuery] string cursor = null, [FromQuery] int take = ModuleConstants.GitHistory.DefaultPageSize, CancellationToken cancellationToken = default)
    {
        var request = new ContentHistoryListOperationRequest
        {
            StoreId = storeId,
            Items = items ?? [],
            Cursor = cursor,
            Take = take,
            BindingErrors = ModelState.GetBindingErrors(),
        };

        return (await contentHistoryOperationService.ListAsync(request, User, cancellationToken)).ToActionResult();
    }

    [HttpGet("{versionId}")]
    public async Task<ActionResult> GetVersion([FromRoute] string versionId, [FromQuery] string storeId, [FromQuery] string item,
        CancellationToken cancellationToken = default)
    {
        var request = new ContentHistoryOperationRequest
        {
            StoreId = storeId,
            Items = item == null ? [] : [item],
            VersionId = versionId,
            BindingErrors = ModelState.GetBindingErrors(),
        };
        var result = await contentHistoryOperationService.ReadVersionAsync(request, User, cancellationToken);

        return result.Succeeded ? Content(result.Value, MediaTypeNames.Application.Json) : result.ToRefusal();
    }

    [HttpPost("{versionId}/restore")]
    [Authorize(ModuleConstants.Security.Permissions.Update)]
    public async Task<ActionResult<ContentSaveResult>> Restore([FromRoute] string versionId, [FromQuery] string storeId,
        [FromQuery(Name = "item")] string[] items, CancellationToken cancellationToken = default)
    {
        var request = new ContentHistoryRestoreOperationRequest
        {
            StoreId = storeId,
            Items = items ?? [],
            VersionId = versionId,
            SaveId = Request.GetOrCreateSaveId(),
            BindingErrors = ModelState.GetBindingErrors(),
        };

        return (await contentHistoryOperationService.RestoreAsync(request, User, cancellationToken)).ToActionResult();
    }
}
