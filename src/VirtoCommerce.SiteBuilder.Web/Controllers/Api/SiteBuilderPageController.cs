using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VirtoCommerce.ContentModule.Core.Model;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.SiteBuilder.Web.Extensions;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.Pages.Core.Search;
using VirtoCommerce.Platform.Core.Common;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Web.Controllers.Api;

[Route("api/site-builder-pages")]
[Authorize]
public class SiteBuilderPageController : Controller
{
    private readonly IGroupedPageService groupedPageService;
    private readonly IGroupedPageSearchService groupedPageSearchService;
    private readonly IAuthorizationService authorizationService;
    private readonly IPageDocumentSearchService pageDocumentSearchService;
    private readonly SiteBuilderPageContentService pageContentService;
    private readonly ILogger<SiteBuilderPageController> logger;
    private readonly IGroupedPageOperationService groupedPageOperationService;

    public SiteBuilderPageController(
        IGroupedPageService groupedPageService,
        IGroupedPageSearchService groupedPageSearchService,
        IAuthorizationService authorizationService,
        IPageDocumentSearchService pageDocumentSearchService,
        SiteBuilderPageContentService pageContentService,
        ILogger<SiteBuilderPageController> logger,
        IGroupedPageOperationService groupedPageOperationService)
    {
        this.groupedPageService = groupedPageService;
        this.groupedPageSearchService = groupedPageSearchService;
        this.authorizationService = authorizationService;
        this.pageDocumentSearchService = pageDocumentSearchService;
        this.pageContentService = pageContentService;
        this.logger = logger;
        this.groupedPageOperationService = groupedPageOperationService;
    }

    [HttpPost("search")]
    public async Task<ActionResult<GroupedSiteBuilderPageSearchResult>> SearchGroups([FromBody] SiteBuilderPageSearchCriteria criteria)
    {
        if (criteria == null)
        {
            return BadRequest("Search criteria are required.");
        }

        var authorizationResult = await authorizationService.AuthorizeAsync(User, criteria, new SiteBuilderAuthorizationRequirement());
        if (!authorizationResult.Succeeded)
        {
            return Forbidden;
        }

        var result = await groupedPageSearchService.SearchAsync(criteria);

        return Ok(result);
    }

    /// <summary>
    /// Pages list where a Multilanguage Page is a single row with its language statuses.
    /// </summary>
    [HttpPost("list")]
    public async Task<ActionResult<SiteBuilderPageListSearchResult>> SearchList(
        [FromBody] SiteBuilderPageSearchCriteria criteria,
        [FromServices] ISiteBuilderPageListService pageListService,
        CancellationToken cancellationToken = default)
    {
        if (criteria == null)
        {
            return BadRequest("Search criteria are required.");
        }

        var authorizationResult = await authorizationService.AuthorizeAsync(User, criteria, new SiteBuilderAuthorizationRequirement());
        if (!authorizationResult.Succeeded)
        {
            return Forbidden;
        }

        return Ok(await pageListService.SearchAsync(criteria, cancellationToken));
    }

    /// <summary>
    /// Pages that already serve the permalink in the given languages; shown as a warning before saving.
    /// </summary>
    [HttpPost("permalink-usage")]
    public async Task<ActionResult<IList<PermalinkUsage>>> FindPermalinkUsage(
        [FromBody] PermalinkUsageRequest request,
        [FromServices] ISiteBuilderPageListService pageListService,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            return BadRequest("Request is required.");
        }

        var authorizationResult = await authorizationService.AuthorizeAsync(
            User, new SiteBuilderPageSearchCriteria { StoreId = request.StoreId }, new SiteBuilderAuthorizationRequirement());
        if (!authorizationResult.Succeeded)
        {
            return Forbidden;
        }

        return Ok(await pageListService.FindPermalinkUsageAsync(request, cancellationToken));
    }

    /// <summary>
    /// Name and permalink for a copy of the page that no page of the store uses yet; Clone saves the copy with them.
    /// </summary>
    [HttpGet("grouped/{groupId}/copy-name")]
    public async Task<ActionResult<PageCopyName>> GetCopyName(
        [FromRoute] string groupId,
        [FromServices] ISiteBuilderPageListService pageListService,
        CancellationToken cancellationToken = default)
    {
        var groupedPage = await groupedPageService.GetNoCloneAsync(groupId);
        if (groupedPage == null)
        {
            return NotFound();
        }

        var authorizationResult = await authorizationService.AuthorizeAsync(User, groupedPage, new SiteBuilderAuthorizationRequirement());
        if (!authorizationResult.Succeeded)
        {
            return Forbidden;
        }

        return Ok(await pageListService.GetCopyNameAsync(groupedPage, cancellationToken));
    }

    [HttpGet("grouped/{groupId}")]
    public async Task<ActionResult<GroupedSiteBuilderPage>> GetGroup([FromRoute] string groupId, [FromQuery] string responseGroup = null)
    {
        var groupedPage = await groupedPageService.GetNoCloneAsync(groupId, responseGroup);
        if (groupedPage == null)
        {
            return NotFound();
        }

        var authorizationResult = await authorizationService.AuthorizeAsync(User, groupedPage, new SiteBuilderAuthorizationRequirement());
        if (!authorizationResult.Succeeded)
        {
            return Forbidden;
        }

        return Ok(groupedPage);
    }

    /// <summary>
    /// Create or update group for page with status Draft in the given group
    /// </summary>
    /// <param name="model">Model of page to update</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns></returns>
    [HttpPut("grouped")]
    [Authorize(ModuleConstants.Security.Permissions.Update)]
    public async Task<ActionResult<GroupedSiteBuilderPage>> UpdateGroup([FromBody] GroupedSiteBuilderPage model, CancellationToken cancellationToken = default)
    {
        var request = new GroupedPageSaveOperationRequest { Page = model };

        return (await groupedPageOperationService.UpdateAsync(request, User, cancellationToken)).ToPageActionResult();
    }

    [HttpPost("grouped")]
    [Authorize(ModuleConstants.Security.Permissions.Create)]
    public async Task<ActionResult<GroupedSiteBuilderPage>> CreateGroup([FromBody] GroupedSiteBuilderPage model, CancellationToken cancellationToken = default)
    {
        var request = new GroupedPageSaveOperationRequest { Page = model };

        return (await groupedPageOperationService.CreateAsync(request, User, cancellationToken)).ToPageActionResult();
    }

    /// <summary>
    /// Create a grouped page with a draft page and its content in a single request.
    /// </summary>
    [HttpPost("create-group-page")]
    [Authorize(ModuleConstants.Security.Permissions.Create)]
    public async Task<ActionResult<GroupedSiteBuilderPage>> CreateGroupPage(
        [FromBody] CreateGroupedPageRequest model,
        CancellationToken cancellationToken = default)
    {
        var request = new GroupedPageCreateOperationRequest { Page = model };

        return (await groupedPageOperationService.CreateWithContentAsync(request, User, cancellationToken)).ToPageActionResult();
    }

    [HttpPost("grouped/archive")]
    [Authorize(ModuleConstants.Security.Permissions.Delete)]
    [ProducesResponseType(typeof(void), StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ArchiveGroups([FromQuery] string[] ids, CancellationToken cancellationToken = default)
    {
        var groupedPages = await groupedPageService.GetAsync(ids);

        var authorizationResult = await authorizationService.AuthorizeAsync(User, groupedPages, new SiteBuilderAuthorizationRequirement());
        if (!authorizationResult.Succeeded)
        {
            return Forbidden;
        }

        foreach (var groupedPage in groupedPages)
        {
            foreach (var page in groupedPage.Pages)
            {
                page.Status = Archived;
            }
        }

        await groupedPageService.SaveChangesAsync(groupedPages);

        return NoContent();
    }

    [HttpPost]
    [Route("grouped/publishing/{groupId}")]
    [Authorize(ModuleConstants.Security.Permissions.Publish)]
    public async Task<ActionResult> PublishGroup([FromRoute] string groupId, [FromQuery] bool publish, CancellationToken cancellationToken = default)
    {
        var request = new GroupedPagePublishOperationRequest { GroupId = groupId, Publish = publish, SaveId = Request.GetOrCreateSaveId() };

        return (await groupedPageOperationService.PublishAsync(request, User, cancellationToken)).ToPageRefusal() ?? Ok();
    }

    [HttpPost("grouped/{groupId}/discard")]
    [Authorize(ModuleConstants.Security.Permissions.Update)]
    public async Task<ActionResult> DiscardGroupDraft([FromRoute] string groupId, CancellationToken cancellationToken = default)
    {
        var request = new GroupedPageDiscardOperationRequest { GroupId = groupId, SaveId = Request.GetOrCreateSaveId() };
        var result = await groupedPageOperationService.DiscardDraftAsync(request, User, cancellationToken);
        if (result.Status == SiteBuilderOperationStatus.Invalid)
        {
            return BadRequest(new { error = result.Error });
        }

        return result.ToPageRefusal() ?? Ok();
    }

    [HttpDelete]
    [Authorize(ModuleConstants.Security.Permissions.Delete)]
    [Route("grouped/{groupId}")]
    public async Task<ActionResult> DeleteGroup([FromRoute] string groupId)
    {
        var groupedPages = await groupedPageService.GetAsync([groupId]);

        var authorizationResult = await authorizationService.AuthorizeAsync(User, groupedPages, new SiteBuilderAuthorizationRequirement());
        if (!authorizationResult.Succeeded)
        {
            return Forbidden;
        }

        var pageDeleted = false;
        var indexDeleted = false;

        try
        {
            await groupedPageService.DeleteAsync([groupId]);
            pageDeleted = true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Couldn't remove group '{GroupId}'", groupId);
        }

        try
        {
            var pagesIds = groupedPages.SelectMany(x => x.Pages.Select(p => p.Id)).ToArray();
            await pageDocumentSearchService.RemoveDocuments(pagesIds);
            indexDeleted = true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Couldn't remove pages from group '{GroupId}'", groupId);
        }

        return Ok(new { pageDeleted, indexDeleted });
    }

    [HttpGet]
    [Route("grouped/publish-status/{groupId}")]
    public async Task<ActionResult<FilePublishStatus>> PublishStatus([FromRoute] string groupId)
    {
        var groupedPage = await groupedPageService.GetNoCloneAsync(groupId);
        if (groupedPage == null)
        {
            return NotFound();
        }

        var authorizationResult = await authorizationService.AuthorizeAsync(User, groupedPage, new SiteBuilderAuthorizationRequirement());
        if (!authorizationResult.Succeeded)
        {
            return Forbidden;
        }

        var result = new FilePublishStatus
        {
            Published = groupedPage.Status == Published,
            HasChanges = groupedPage.HasChanges,
        };

        return Ok(result);
    }

    // Retain the published direct-call signature without exposing a second HTTP action.
    [NonAction]
    public Task GetPageContent(string groupId, bool draft, CancellationToken cancellationToken)
    {
        return GetPageContent(groupId, draft, includeVersion: false, cancellationToken);
    }

    [HttpGet("grouped/{groupId}/content")]
    public async Task GetPageContent([FromRoute] string groupId, [FromQuery] bool draft = true,
        [FromQuery] bool includeVersion = false, CancellationToken cancellationToken = default)
    {
        Response.ContentType = "application/json; charset=utf-8";
        var group = await groupedPageService.GetByIdAsync(groupId);
        if (group == null)
        {
            Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        var authorizationResult = await authorizationService.AuthorizeAsync(User, group, new SiteBuilderAuthorizationRequirement());
        if (!authorizationResult.Succeeded)
        {
            Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        SiteBuilderContentSnapshot snapshot;
        try
        {
            snapshot = await pageContentService.LoadGroupContentAsync(group, draft, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.Headers.ETag = snapshot.ETag;
        Response.Headers.CacheControl = "no-store";
        if (includeVersion)
        {
            await WriteVersionedContentResponseAsync(snapshot.Content ?? ModuleConstants.DefaultPageContent, snapshot.ETag, cancellationToken);
        }
        else if (snapshot.Content != null)
        {
            await Response.WriteAsync(snapshot.Content, cancellationToken);
        }
        else
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    private async Task WriteVersionedContentResponseAsync(string content, string eTag, CancellationToken cancellationToken)
    {
        await using (var json = new Utf8JsonWriter(Response.BodyWriter))
        {
            json.WriteStartObject();
            json.WriteString("content", content);
            json.WriteString("eTag", eTag);
            json.WriteEndObject();
        }
        await Response.BodyWriter.FlushAsync(cancellationToken);
    }

    [NonAction]
    public Task<IActionResult> SavePageContent(string groupId, CancellationToken cancellationToken)
    {
        return SavePageContent(groupId, Request.Headers.IfMatch.ToString(), includeVersion: false, cancellationToken);
    }

    [HttpPost("grouped/{groupId}/content")]
    [Authorize(ModuleConstants.Security.Permissions.Update)]
    [ProducesResponseType(typeof(UpdatePageContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SavePageContent([FromRoute] string groupId,
        [FromHeader(Name = "If-Match")] string ifMatch = null, [FromQuery] bool includeVersion = false,
        CancellationToken cancellationToken = default)
    {
        var request = new GroupedPageContentSaveOperationRequest
        {
            GroupId = groupId,
            Content = await ReadRequestContentAsync(cancellationToken),
            IfMatch = ifMatch,
            SaveId = Request.GetOrCreateSaveId(),
        };

        return ToContentSaveResponse(await groupedPageOperationService.SaveRawContentAsync(request, User, cancellationToken), includeVersion);
    }

    [NonAction]
    public Task<IActionResult> SavePageContentJson(string groupId, UpdatePageContentRequest model, CancellationToken cancellationToken)
    {
        return SavePageContentJson(groupId, model, Request.Headers.IfMatch.ToString(), includeVersion: false, cancellationToken);
    }

    /// <summary>
    /// Save page content from a JSON body. Mirror of <see cref="SavePageContent(string, string, bool, CancellationToken)"/> but accepts the page JSON
    /// inside a wrapping JSON object (<c>{ "content": "..." }</c>) instead of as a raw stream, so it is callable
    /// from generic JSON-body API tools (e.g. the AI agent tool runner).
    /// </summary>
    [HttpPost("grouped/{groupId}/content-json")]
    [Authorize(ModuleConstants.Security.Permissions.Update)]
    [ProducesResponseType(typeof(UpdatePageContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SavePageContentJson(
        [FromRoute] string groupId,
        [FromBody] UpdatePageContentRequest model,
        [FromHeader(Name = "If-Match")] string ifMatch = null,
        [FromQuery] bool includeVersion = false,
        CancellationToken cancellationToken = default)
    {
        var request = new GroupedPageContentSaveOperationRequest
        {
            GroupId = groupId,
            Content = model?.Content,
            ETag = model?.ETag,
            IfMatch = ifMatch,
            SaveId = Request.GetOrCreateSaveId(),
        };

        return ToContentSaveResponse(await groupedPageOperationService.SaveContentAsync(request, User, cancellationToken), includeVersion);
    }

    private IActionResult ToContentSaveResponse(SiteBuilderOperationResult<UpdatePageContentResult> result, bool includeVersion)
    {
        if (!result.Succeeded)
        {
            return result.ToPageRefusal();
        }

        Response.Headers.ETag = result.Value.ETag;

        return includeVersion ? Ok(result.Value) : NoContent();
    }

    [HttpPost("grouped/{targetGroupId}/content/{sourceGroupId}")]
    [Authorize(ModuleConstants.Security.Permissions.Update)]
    public async Task<IActionResult> CopyPageContent(
        [FromRoute] string targetGroupId,
        [FromRoute] string sourceGroupId,
        CancellationToken cancellationToken = default)
    {
        var request = new GroupedPageContentCopyOperationRequest { GroupId = targetGroupId, SourceGroupId = sourceGroupId };

        return (await groupedPageOperationService.CopyContentAsync(request, User, cancellationToken)).ToPageRefusal() ?? NoContent();
    }

    private async Task<string> ReadRequestContentAsync(CancellationToken cancellationToken)
    {
        // Validate the complete request before a draft row is created. A blank or truncated write would
        // otherwise shadow the published page and could later replace it during publishing.
        using var reader = new StreamReader(
            Request.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static ActionResult Forbidden => new ObjectResult(new { })
    {
        StatusCode = (int)HttpStatusCode.Forbidden,
    };
}
