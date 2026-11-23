using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.SiteBuilder.Web.Extensions;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Web.Controllers.Api;

[Route("api/site-builder-multilanguage-pages")]
[Authorize]
public class MultilanguagePageController(
    IMultilanguagePageService multilanguagePageService,
    IMultilanguagePageSearchService multilanguagePageSearchService,
    IAuthorizationService authorizationService,
    IMultilanguagePageOperationService multilanguagePageOperationService)
    : Controller
{
    private const string ConcurrentChangeMessage = "The multilanguage page was changed by another request. Reload it and try again.";

    [HttpPost("search")]
    public async Task<ActionResult<MultilanguagePageSearchResult>> Search([FromBody] MultilanguagePageSearchCriteria criteria)
    {
        if (criteria == null)
        {
            return BadRequest("Search criteria are required.");
        }

        if (!await IsAuthorizedAsync(criteria))
        {
            return Forbidden;
        }

        return Ok(await multilanguagePageSearchService.SearchAsync(criteria));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<MultilanguagePage>> Get([FromRoute] string id)
    {
        var page = await multilanguagePageService.GetNoCloneAsync(id);
        if (page == null)
        {
            return NotFound();
        }

        return await IsAuthorizedAsync(page) ? Ok(page) : Forbidden;
    }

    [HttpPost]
    [Authorize(ModuleConstants.Security.Permissions.Create)]
    public async Task<ActionResult<MultilanguagePage>> Create([FromBody] MultilanguagePage model)
    {
        var request = new MultilanguagePageSaveOperationRequest { Page = model };

        return (await multilanguagePageOperationService.CreateAsync(request, User, HttpContext.RequestAborted)).ToPageActionResult();
    }

    /// <summary>
    /// Updates the root; the shared name and inherited settings are propagated to the language versions.
    /// </summary>
    [HttpPut]
    [Authorize(ModuleConstants.Security.Permissions.Update)]
    public async Task<ActionResult<MultilanguagePage>> Update([FromBody] MultilanguagePage model)
    {
        var request = new MultilanguagePageSaveOperationRequest { Page = model };

        return (await multilanguagePageOperationService.UpdateAsync(request, User, HttpContext.RequestAborted)).ToPageActionResult();
    }

    /// <summary>
    /// Deletes a root that has no language versions. A root with versions is archived with archive-all instead.
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(ModuleConstants.Security.Permissions.Delete)]
    [ProducesResponseType(typeof(void), StatusCodes.Status204NoContent)]
    public async Task<ActionResult> Delete([FromRoute] string id)
    {
        var page = await multilanguagePageService.GetNoCloneAsync(id);
        if (page == null)
        {
            return NotFound();
        }

        if (!await IsAuthorizedAsync(page))
        {
            return Forbidden;
        }

        var languages = await multilanguagePageService.GetLanguagesAsync(id);
        if (languages.Any(x => x.Page != null))
        {
            return BadRequest($"Multilanguage page '{id}' has language versions: archive them instead.");
        }

        try
        {
            await multilanguagePageService.DeleteAsync([id]);
        }
        catch (DbUpdateException)
        {
            // A language version created meanwhile references the page (foreign key).
            return Conflict(ConcurrentChangeMessage);
        }

        return NoContent();
    }

    [HttpGet("{id}/languages")]
    public Task<ActionResult<IList<MultilanguagePageLanguage>>> GetLanguages([FromRoute] string id, CancellationToken cancellationToken = default)
    {
        return ExecuteForRootAsync(id, () => multilanguagePageService.GetLanguagesAsync(id, cancellationToken));
    }

    /// <summary>
    /// Creates a draft version for a missing store language.
    /// </summary>
    /// <param name="id">Multilanguage page id</param>
    /// <param name="cultureName">Store language to create</param>
    /// <param name="copyContent">Start from the content of the default language version</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpPost("{id}/languages/{cultureName}")]
    [Authorize(ModuleConstants.Security.Permissions.Create)]
    public async Task<ActionResult<GroupedSiteBuilderPage>> CreateLanguage(
        [FromRoute] string id,
        [FromRoute] string cultureName,
        [FromQuery] bool copyContent = true,
        CancellationToken cancellationToken = default)
    {
        var request = new MultilanguagePageLanguageOperationRequest { MultilanguagePageId = id, CultureName = cultureName, CopyContent = copyContent };

        return (await multilanguagePageOperationService.CreateLanguageAsync(request, User, cancellationToken)).ToPageActionResult();
    }

    /// <summary>
    /// Makes an existing page a language version of the multilanguage page.
    /// </summary>
    [HttpPost("{id}/link/{groupId}")]
    [Authorize(ModuleConstants.Security.Permissions.Update)]
    public async Task<ActionResult<GroupedSiteBuilderPage>> Link([FromRoute] string id, [FromRoute] string groupId, CancellationToken cancellationToken = default)
    {
        var request = new MultilanguagePageLinkOperationRequest { MultilanguagePageId = id, GroupId = groupId };

        return (await multilanguagePageOperationService.LinkAsync(request, User, cancellationToken)).ToPageActionResult();
    }

    /// <summary>
    /// Turns a Landing Page into a Multilanguage Page with the page as its default language version.
    /// </summary>
    [HttpPost("convert/{groupId}")]
    [Authorize(ModuleConstants.Security.Permissions.Create)]
    // The existing page changes too, as with Link.
    [Authorize(ModuleConstants.Security.Permissions.Update)]
    public async Task<ActionResult<MultilanguagePage>> Convert([FromRoute] string groupId, CancellationToken cancellationToken = default)
    {
        var request = new MultilanguagePageVersionOperationRequest { GroupId = groupId };

        return (await multilanguagePageOperationService.ConvertAsync(request, User, cancellationToken)).ToPageActionResult();
    }

    /// <summary>
    /// Marks an outdated translation as matching the current source language publication.
    /// </summary>
    [HttpPost("mark-up-to-date/{groupId}")]
    [Authorize(ModuleConstants.Security.Permissions.Update)]
    public async Task<ActionResult<GroupedSiteBuilderPage>> MarkUpToDate([FromRoute] string groupId, CancellationToken cancellationToken = default)
    {
        var request = new MultilanguagePageMarkUpToDateOperationRequest { GroupId = groupId };

        return (await multilanguagePageOperationService.MarkUpToDateAsync(request, User, cancellationToken)).ToPageActionResult();
    }

    /// <summary>
    /// Turns a language version back into a Landing Page.
    /// </summary>
    [HttpPost("unlink/{groupId}")]
    [Authorize(ModuleConstants.Security.Permissions.Update)]
    public async Task<ActionResult<GroupedSiteBuilderPage>> Unlink([FromRoute] string groupId, CancellationToken cancellationToken = default)
    {
        var request = new MultilanguagePageVersionOperationRequest { GroupId = groupId };

        return (await multilanguagePageOperationService.UnlinkAsync(request, User, cancellationToken)).ToPageActionResult();
    }

    /// <summary>
    /// Applies the root values of the given fields (Permalink, Visibility, UserGroups, OrganizationId, StartDate, EndDate)
    /// to every language version, dropping their overrides of those fields.
    /// </summary>
    [HttpPost("{id}/reset-overrides")]
    [Authorize(ModuleConstants.Security.Permissions.Update)]
    public async Task<ActionResult<IList<string>>> ResetOverrides([FromRoute] string id, [FromBody] IList<string> fields,
        CancellationToken cancellationToken = default)
    {
        var request = new MultilanguagePageResetOverridesOperationRequest { MultilanguagePageId = id, Fields = fields };

        return (await multilanguagePageOperationService.ResetOverridesAsync(request, User, cancellationToken)).ToPageActionResult();
    }

    /// <summary>
    /// Copies the Multilanguage Page with every language version as drafts.
    /// </summary>
    [HttpPost("{id}/clone")]
    [Authorize(ModuleConstants.Security.Permissions.Create)]
    public async Task<ActionResult<MultilanguagePage>> Clone([FromRoute] string id, CancellationToken cancellationToken = default)
    {
        var request = new MultilanguagePageOperationRequest { MultilanguagePageId = id };

        return (await multilanguagePageOperationService.CloneAsync(request, User, cancellationToken)).ToPageActionResult();
    }

    [HttpPost("{id}/publish-all")]
    [Authorize(ModuleConstants.Security.Permissions.Publish)]
    public async Task<ActionResult<MultilanguagePageBatchResult>> PublishAll([FromRoute] string id, CancellationToken cancellationToken = default)
    {
        var request = new MultilanguagePagePublishAllOperationRequest { MultilanguagePageId = id };

        return (await multilanguagePageOperationService.PublishAllAsync(request, User, cancellationToken)).ToPageActionResult();
    }

    [HttpPost("{id}/archive-all")]
    [Authorize(ModuleConstants.Security.Permissions.Delete)]
    public Task<ActionResult<MultilanguagePageBatchResult>> ArchiveAll([FromRoute] string id, CancellationToken cancellationToken = default)
    {
        return ExecuteForRootAsync(id, () => multilanguagePageService.ArchiveAllAsync(id, cancellationToken));
    }

    private async Task<ActionResult<T>> ExecuteForRootAsync<T>(string id, Func<Task<T>> action)
    {
        var page = await multilanguagePageService.GetNoCloneAsync(id);
        if (page == null)
        {
            return NotFound();
        }

        if (!await IsAuthorizedAsync(page))
        {
            return Forbidden;
        }

        try
        {
            return Ok(await action());
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException)
        {
            return BadRequest(ex.Message);
        }
        catch (DbUpdateException)
        {
            // A concurrent request took the language (unique index) or removed the page (foreign key); the data is intact.
            return Conflict(ConcurrentChangeMessage);
        }
    }

    private async Task<bool> IsAuthorizedAsync(object resource)
    {
        var result = await authorizationService.AuthorizeAsync(User, resource, new SiteBuilderAuthorizationRequirement());
        return result.Succeeded;
    }

    private static ActionResult Forbidden => new ObjectResult(new { })
    {
        StatusCode = (int)HttpStatusCode.Forbidden,
    };
}
