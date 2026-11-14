using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;

namespace VirtoCommerce.SiteBuilder.MCP;

// A publish or an unpublish of a page stored as a file, as publish_page and unpublish_page run it.
public sealed record FilePageShipCall(string StoreId, string PageId, bool Publish, bool Rebase, string DraftOwner,
    IDictionary<string, string> ExpectedVersions, bool DryRun);

// The operation's dry run, then its write, bound to what the dry run showed. In git the draft is a branch of its
// editor, named after the user, and an approved change runs as the approver: the dry run answers whose branch it ships
// (draft_owner) and the commit it is at, and the write ships that branch at that commit. In blob storage there is one
// draft for every editor and no lock over it: its version is the hash of the content, read right before the write.
public class FilePageShipping(
    IAuthorizationService authorizationService,
    IFilePageOperationService filePageOperationService,
    ISiteBuilderContentQueryService contentQueryService,
    ILogger<FilePageShipping> logger)
{
    public const string DraftOwnerArgument = "draft_owner";

    // In git a conflict is an outcome, not a failure: the pull request may be open, so it is answered with the ways out,
    // and nothing is tried again.
    public async Task<SiteBuilderChange> ShipAsync(ClaimsPrincipal user, FilePageShipCall call, CancellationToken cancellationToken)
    {
        var page = await FilePages.LoadAsync(authorizationService, user, call.StoreId, call.PageId);
        var owner = string.IsNullOrWhiteSpace(call.DraftOwner) ? user?.Identity?.Name : call.DraftOwner.Trim();
        var change = new SiteBuilderChange
        {
            Action = call.Publish ? "publish_page" : "unpublish_page",
            ObjectType = ChangePreview.FilePage,
            ObjectId = page.Path,
            ObjectName = page.PageId,
            PageId = page.PageId,
        };

        var (_, result) = await SiteBuilderChanges.RunAsync(change,
            isDryRun => FilePages.RunAsync(logger, () => RunAsync(user, page, call, isDryRun ? null : change, owner, cancellationToken)),
            x => EnsureShipped(x, page, call.Publish),
            call.DryRun,
            includeWarnings: true,
            checkAsync: x => PinAsync(user, page, call, change, owner, x, cancellationToken));

        if (!change.DryRun)
        {
            AddShipment(change, result.Value);
        }

        return change;
    }

    // The write ships the branch and the commit the dry run pinned; a dry run reads the branch of the draft owner.
    private Task<SiteBuilderOperationResult<FilePageShipment>> RunAsync(ClaimsPrincipal user, FilePage page, FilePageShipCall call, SiteBuilderChange pinned,
        string owner, CancellationToken cancellationToken)
    {
        var inGit = pinned?.DraftOwner != null;
        var draftOwner = pinned == null ? owner : pinned.DraftOwner;
        var draftHead = inGit ? ReviewedVersions.Of(pinned, page.PageId) : null;

        return call.Publish
            ? filePageOperationService.PublishAsync(new FilePagePublishOperationRequest
            {
                StoreId = page.StoreId,
                ContentType = page.ContentType,
                Path = page.Path,
                Rebase = call.Rebase,
                DraftOwner = draftOwner,
                ExpectedDraftHead = draftHead,
                DryRun = pinned == null,
                ReportImpact = true,
            }, user, cancellationToken)
            : filePageOperationService.UnpublishAsync(new FilePageShipOperationRequest
            {
                StoreId = page.StoreId,
                ContentType = page.ContentType,
                Path = page.Path,
                DraftOwner = draftOwner,
                ExpectedDraftHead = draftHead,
                DryRun = pinned == null,
                ReportImpact = true,
            }, user, cancellationToken);
    }

    // In git the version is the commit the draft branch is at, and the call has to name whose branch it is, as the
    // approver's own branch is another one; in blob storage it is the hash of the content the write takes.
    private async Task PinAsync(ClaimsPrincipal user, FilePage page, FilePageShipCall call, SiteBuilderChange change, string owner,
        SiteBuilderOperationResult check, CancellationToken cancellationToken)
    {
        var item = check.Preview?.Objects.FirstOrDefault(x => x.ObjectType == ChangePreview.FilePage);
        string version;
        if (item?.DraftBranch != null)
        {
            if (call.ExpectedVersions != null && string.IsNullOrWhiteSpace(call.DraftOwner))
            {
                throw new McpException($"Nothing was changed: pass {DraftOwnerArgument} as the dry run answers it, the user whose draft branch of " +
                    $"page '{page.PageId}' was reviewed. Without it an approver would ship their own draft.");
            }

            change.DraftOwner = owner;
            version = item.DraftHead;
        }
        else
        {
            version = FilePages.VersionOf(await FilePages.ReadAsync(contentQueryService, logger, user, page, draft: call.Publish, cancellationToken));
        }

        ReviewedVersions.Pin(change, [new KeyValuePair<string, string>(page.PageId, version)], call.ExpectedVersions, call.DryRun);
    }

    private static void AddShipment(SiteBuilderChange change, FilePageShipment shipment)
    {
        change.Shipment = FilePageShipmentRow.From(shipment);
        switch (shipment?.State)
        {
            case GitPublishState.Conflict:
                change.AddWarning(shipment.ConflictError);
                change.AddWarning(shipment.CanRebase
                    ? "Nothing was published and nothing was tried again. Tell the user the two ways out: re-read the page with " +
                      "search_site_pages, apply the change again with edit_page_draft and publish it; or call publish_page again " +
                      "with rebase: true, which replaces what changed on the published page since this draft began. That call " +
                      "waits for a new approval."
                    : "Nothing was unpublished. Re-read the page with search_site_pages and tell the user; the pull request is in shipment.");
                break;
            case GitPublishState.Pending:
                change.AddWarning("The pull request merges itself once the repository's checks pass; customers see the change after that.");
                break;
            case GitPublishState.AwaitingMerge:
                change.AddWarning("The pull request waits for the repository's checks and does not merge itself: once they pass, " +
                    $"call {change.Action} with dryRun again and then with its {ReviewedVersions.Argument} (it needs approval again), or merge it in GitHub.");
                break;
        }
    }

    private static void EnsureShipped(SiteBuilderOperationResult result, FilePage page, bool publish)
    {
        if (!publish && result.Status == SiteBuilderOperationStatus.NotFound)
        {
            throw new McpException($"Page '{page.PageId}' is not published in store '{page.StoreId}', so there is nothing to unpublish.");
        }

        if (result.Status == SiteBuilderOperationStatus.Invalid && result.Errors?.Count > 0)
        {
            throw new McpException($"Page '{page.PageId}' is not a page document that can be published: {string.Join("; ", result.Errors)}. " +
                "Fix it with edit_page_draft or in the designer.");
        }

        if (result.Status == SiteBuilderOperationStatus.Conflict)
        {
            throw new McpException($"The draft of page '{page.PageId}' changed since it was reviewed, so nothing was " +
                $"{(publish ? "published" : "unpublished")}. Call {(publish ? "publish_page" : "unpublish_page")} with dryRun again, show " +
                $"the user what it would do now, and send the new {ReviewedVersions.Argument} for approval.");
        }

        FilePages.EnsureSucceeded(result, page, publish ? "publish" : "unpublish");
    }
}
