using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// Dropping a draft loses its author's unpublished work, and customers see nothing of it either way: the user confirms
// it in the chat, and a client that cannot ask does not discard. What is discarded is named by kind, so that other
// kinds of drafts can be added without changing the arguments of the pages.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class DraftTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IGroupedPageOperationService groupedPageOperationService)
{
    public const string PageKind = "page";

    [McpServerTool(Name = "discard_draft", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [McpRequiresConfirmation("Discard the unpublished changes of {kind} {page_id}? The draft is deleted and the page goes back to its published version.",
        Fallback = McpConfirmationFallback.Refuse)]
    [Description("Discard the draft of a database page: its unpublished changes are deleted and the page is its published " +
        "version again. A page that was never published has nothing to go back to and is refused. The user confirms the " +
        "call in the chat; a client that cannot ask refuses it, and the user discards the draft in the designer instead. " +
        "Returns the changes the discard undoes. Call it with dryRun first to show them.")]
    public async Task<SiteBuilderChange> DiscardDraft(
        RequestContext<CallToolRequestParams> context,
        [Description("What to discard the draft of: 'page'.")] string kind,
        [Description("Id of a Landing Page or of a language version (search_site_pages).")] string page_id,
        [Description("true to return what the discard would undo, without discarding.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (kind != PageKind)
        {
            throw new McpException($"kind must be '{PageKind}'.");
        }

        var group = await groupedPageService.LoadPageAsync(authorizationService, context.User, page_id);
        var change = new SiteBuilderChange
        {
            Action = "discard_draft",
            ObjectType = ChangePreview.Page,
            ObjectId = group.Id,
            ObjectName = group.Name,
            PageId = group.Id,
            MultilanguagePageId = group.MultilanguagePageId,
        };

        await SiteBuilderChanges.RunAsync(change,
            isDryRun => groupedPageOperationService.DiscardDraftAsync(new GroupedPageDiscardOperationRequest
            {
                GroupId = group.Id,
                SaveId = Guid.NewGuid().ToString("N"),
                DryRun = isDryRun,
            }, context.User, cancellationToken),
            x => EnsureDiscarded(x, group),
            dryRun);

        return change;
    }

    private static void EnsureDiscarded(SiteBuilderOperationResult result, GroupedSiteBuilderPage group)
    {
        if (result.Status == SiteBuilderOperationStatus.Invalid && group.Pages.All(x => x.Status != PageStatuses.Published))
        {
            throw new McpException($"Page '{group.Id}' was never published, so its draft is all there is and is not discarded. Edit it with edit_page_draft instead.");
        }

        SiteBuilderChanges.EnsureSucceeded(result, $"Page '{group.Id}'", PageLookup.ForbiddenPage(group));
    }
}
