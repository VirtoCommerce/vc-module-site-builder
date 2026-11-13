using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.SiteBuilder.Web.Extensions;
using VirtoCommerce.SiteBuilder.Web.Models;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Web.Controllers.Api;

[Route("api/site-builder-shared-components")]
[Authorize]
[method: ActivatorUtilitiesConstructor]
public class SiteBuilderSharedComponentsController(
    ISiteBuilderSharedComponentService sharedComponentService,
    ISiteBuilderSharedComponentContentService sharedComponentContentService,
    ISiteBuilderSharedComponentUsageService sharedComponentUsageService,
    IAuthorizationService authorizationService,
    ISiteBuilderSharedComponentOperationService sharedComponentOperationService)
    : Controller
{
    // Preserve the published constructor for extensions; request services supply the new dependencies.
    public SiteBuilderSharedComponentsController(
        ISiteBuilderSharedComponentService sharedComponentService,
        ISiteBuilderSharedComponentSearchService sharedComponentSearchService,
        ISiteBuilderSharedComponentContentService sharedComponentContentService,
        ISiteBuilderSharedComponentUsageService sharedComponentUsageService,
        IAuthorizationService authorizationService)
        : this(sharedComponentService, sharedComponentContentService, sharedComponentUsageService, authorizationService, null)
    {
    }

    private ISiteBuilderSharedComponentOperationService Operations =>
        sharedComponentOperationService ?? HttpContext.RequestServices.GetRequiredService<ISiteBuilderSharedComponentOperationService>();

    [HttpPost("search")]
    public async Task<ActionResult<SiteBuilderSharedComponentSearchResult>> Search(
        [FromBody] SiteBuilderSharedComponentSearchCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        return (await Operations.SearchAsync(criteria, User, cancellationToken)).ToPageActionResult();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<SiteBuilderSharedComponent>> Get(
        [FromRoute] string id,
        CancellationToken cancellationToken = default)
    {
        var request = new SharedComponentOperationRequest { Id = id };

        return (await Operations.GetAsync(request, User, cancellationToken)).ToPageActionResult();
    }

    [HttpPost]
    [Authorize(ModuleConstants.Security.Permissions.SharedComponentsCreate)]
    [ProducesResponseType(typeof(SiteBuilderSharedComponent), StatusCodes.Status201Created)]
    public async Task<ActionResult<SiteBuilderSharedComponent>> Create(
        [FromBody] SiteBuilderSharedComponentCreateModel request,
        CancellationToken cancellationToken = default)
    {
        var operationRequest = new SharedComponentCreateOperationRequest
        {
            StoreId = request?.StoreId,
            Name = request?.Name,
            Content = request?.Content,
        };
        var result = await Operations.CreateAsync(operationRequest, User, cancellationToken);

        return result.Succeeded ? CreatedAtAction(nameof(Get), new { id = result.Value.Id }, result.Value) : result.ToPageRefusal();
    }

    [HttpPut("{id}")]
    [Authorize(ModuleConstants.Security.Permissions.SharedComponentsUpdate)]
    public async Task<ActionResult<SiteBuilderSharedComponent>> Update(
        [FromRoute] string id,
        [FromBody] SiteBuilderSharedComponentUpdateModel request,
        CancellationToken cancellationToken = default)
    {
        var operationRequest = new SharedComponentUpdateOperationRequest { Id = id, StoreId = request?.StoreId, Name = request?.Name };

        return (await Operations.UpdateAsync(operationRequest, User, cancellationToken)).ToPageActionResult();
    }

    [HttpDelete("{id}")]
    [Authorize(ModuleConstants.Security.Permissions.SharedComponentsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(SiteBuilderSharedComponent), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Delete(
        [FromRoute] string id,
        CancellationToken cancellationToken = default)
    {
        var component = await sharedComponentService.GetByIdAsync(id);
        if (component == null)
        {
            return NotFound();
        }

        if (!await IsAuthorizedAsync(component))
        {
            return Forbidden;
        }

        var includeUsagePages = await CanReadPagesAsync(component);
        await EnrichUsageAsync([component], includeUsagePages, cancellationToken);
        if (component.UsageCount > 0)
        {
            return Conflict(component);
        }

        try
        {
            if (!await sharedComponentService.TryDeleteAsync(component, cancellationToken))
            {
                return NotFound();
            }
        }
        catch (DbUpdateException)
        {
            await EnrichUsageAsync([component], includeUsagePages, cancellationToken);
            if (component.UsageCount > 0)
            {
                return Conflict(component);
            }

            throw;
        }

        return NoContent();
    }

    [HttpGet("{id}/content")]
    [ProducesResponseType(typeof(JObject), StatusCodes.Status200OK)]
    public async Task<ActionResult> GetContent(
        [FromRoute] string id,
        CancellationToken cancellationToken = default)
    {
        var component = await sharedComponentService.GetByIdAsync(id);
        if (component == null)
        {
            return NotFound();
        }

        if (!await IsAuthorizedAsync(component))
        {
            return Forbidden;
        }

        var content = await sharedComponentContentService.TryLoadContentAsync(component, cancellationToken);
        return string.IsNullOrWhiteSpace(content)
            ? NotFound()
            : Content(content, "application/json");
    }

    [HttpPut("{id}/content")]
    [HttpPost("{id}/content")]
    [Authorize(ModuleConstants.Security.Permissions.SharedComponentsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> SaveContent(
        [FromRoute] string id,
        [FromBody] JObject content,
        CancellationToken cancellationToken = default)
    {
        var request = new SharedComponentContentSaveOperationRequest { Id = id, Content = content };

        return (await Operations.SaveContentAsync(request, User, cancellationToken)).ToPageRefusal() ?? NoContent();
    }

    private Task EnrichUsageAsync(
        IList<SiteBuilderSharedComponent> components,
        bool includePages,
        CancellationToken cancellationToken) =>
        sharedComponentUsageService.ApplyUsageAsync(components, includePages, cancellationToken);

    private async Task<bool> IsAuthorizedAsync(object resource)
    {
        var result = await authorizationService.AuthorizeAsync(
            User,
            resource,
            new SiteBuilderAuthorizationRequirement(ModuleConstants.Security.Permissions.SharedComponentsRead));
        return result.Succeeded;
    }

    private async Task<bool> CanReadPagesAsync(object resource)
    {
        var result = await authorizationService.AuthorizeAsync(User, resource, new SiteBuilderAuthorizationRequirement());
        return result.Succeeded;
    }

    private static ActionResult Forbidden => new ObjectResult(new { })
    {
        StatusCode = (int)HttpStatusCode.Forbidden,
    };
}
