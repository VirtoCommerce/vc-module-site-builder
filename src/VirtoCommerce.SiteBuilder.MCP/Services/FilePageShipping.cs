using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

public sealed record FilePageShipCall(string StoreId, string PageId, bool Publish, bool Rebase, string DraftOwner,
    IDictionary<string, string> ExpectedVersions, bool DryRun);

// The write is bound to what the dry run showed. In git each editor drafts on their own branch and an approved change
// runs as the approver, so the dry run pins whose branch and which commit, and the write ships it from a branch of its
// own so later draft commits stay out. Blob storage has one unlocked draft, pinned by the hash of its content.
public class FilePageShipping(
    IAuthorizationService authorizationService,
    IFilePageOperationService filePageOperationService,
    ISiteBuilderContentQueryService contentQueryService,
    ILogger<FilePageShipping> logger)
{
    public const string DraftOwnerArgument = "draft_owner";

    // Pins the published content an unpublish in git removes, next to the draft head pinned under the page id.
    private const string PublishedKeyPrefix = "published:";

    // A git conflict is answered as an outcome with the ways out, since the pull request may already be open; nothing
    // is retried.
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

    private Task<SiteBuilderOperationResult<FilePageShipment>> RunAsync(ClaimsPrincipal user, FilePage page, FilePageShipCall call, SiteBuilderChange pinned,
        string owner, CancellationToken cancellationToken)
    {
        var inGit = pinned?.DraftOwner != null;
        var draftOwner = pinned == null ? owner : pinned.DraftOwner;
        var version = pinned == null ? null : ReviewedVersions.Of(pinned, page.PageId);
        var draftHead = inGit ? version : null;
        var publishedVersion = inGit && !call.Publish ? ReviewedVersions.Of(pinned, PublishedKeyPrefix + page.PageId) : null;
        var contentVersion = inGit ? publishedVersion : version;
        var noDraftBranch = inGit && draftHead == null;

        return call.Publish
            ? filePageOperationService.PublishAsync(new FilePagePublishOperationRequest
            {
                StoreId = page.StoreId,
                ContentType = page.ContentType,
                Path = page.Path,
                Rebase = call.Rebase,
                DraftOwner = draftOwner,
                ExpectedDraftHead = draftHead,
                ExpectNoDraftBranch = noDraftBranch,
                ExpectedContentVersion = contentVersion,
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
                ExpectNoDraftBranch = noDraftBranch,
                ExpectedContentVersion = contentVersion,
                DryRun = pinned == null,
                ReportImpact = true,
            }, user, cancellationToken);
    }

    private async Task PinAsync(ClaimsPrincipal user, FilePage page, FilePageShipCall call, SiteBuilderChange change, string owner,
        SiteBuilderOperationResult check, CancellationToken cancellationToken)
    {
        var item = check.Preview?.Objects.FirstOrDefault(x => x.ObjectType == ChangePreview.FilePage);
        string version;
        var published = new List<KeyValuePair<string, string>>();
        if (item?.DraftBranch != null)
        {
            if (call.ExpectedVersions != null && string.IsNullOrWhiteSpace(call.DraftOwner))
            {
                throw new McpException($"Nothing was changed: pass {DraftOwnerArgument} as the dry run answers it, the user whose draft branch of " +
                    $"page '{page.PageId}' was reviewed. Without it an approver would ship their own draft.");
            }

            change.DraftOwner = owner;
            version = item.DraftHead;
            if (call.Publish)
            {
                change.AddWarning($"The write ships commit {version} of the draft branch {item.DraftBranch} from a publish branch of its own: " +
                    "commits made to the draft branch after this review are not published with it.");
            }
            else
            {
                published.Add(new KeyValuePair<string, string>(PublishedKeyPrefix + page.PageId, item.ETag));
                change.AddWarning($"The write deletes the page on the draft branch {item.DraftBranch}" +
                    (version == null ? ", which it cuts from what is published," : $" at commit {version}") +
                    " and ships that deletion from a publish branch of its own: commits made to the draft branch after this review are not published with it.");
            }
        }
        else
        {
            version = FilePages.VersionOf(await FilePages.ReadAsync(contentQueryService, logger, user, page, draft: call.Publish, cancellationToken));
        }

        ReviewedVersions.Pin(change, [new KeyValuePair<string, string>(page.PageId, version), .. published], call.ExpectedVersions, call.DryRun);
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
