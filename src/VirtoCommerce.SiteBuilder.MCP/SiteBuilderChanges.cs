using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;

namespace VirtoCommerce.SiteBuilder.MCP;

// What a page write did or would do: the fields of each page it touches, "from → to", and whether customers see it.
public sealed class SiteBuilderChange : McpChangePreview, IReviewedChange
{
    // The write reaches what the storefront serves: a publish or unpublish, or a change of a page that has a published version.
    [JsonPropertyName("affects_published")]
    public bool AffectsPublished { get; set; }

    // The page the write created or changed: a Landing Page or a language version.
    [JsonPropertyName("page_id")]
    public string PageId { get; set; }

    [JsonPropertyName("multilanguage_page_id")]
    public string MultilanguagePageId { get; set; }

    // A created page's sections, in order.
    [JsonPropertyName("sections")]
    public IList<SectionRow> Sections { get; set; }

    // A publish or unpublish of a page stored as a file: where it stands, a pull request in git.
    [JsonPropertyName("shipment")]
    public FilePageShipmentRow Shipment { get; set; }

    // A publish, unpublish, discard or settings change: the versions of what it takes, by page or file. Sent back
    // unchanged, the write takes exactly these and is refused once one moved.
    [JsonPropertyName("expected_versions")]
    public IDictionary<string, string> ExpectedVersions { get; set; }

    // A publish or unpublish of a page stored as a file in git: whose draft branch it ships. Sent back unchanged, so that
    // an approver ships the requester's draft and not their own.
    [JsonPropertyName("draft_owner")]
    public string DraftOwner { get; set; }
}

// Runs a write of an operation service the way every page tool does: the operation's own dry run first, as the user of
// the call, then the write. The dry run gives the preview for the agent and the approver, and refuses an unpublished_only
// call that touches a published page before anything is written; the write checks unpublished_only again under its own
// lock (RequireUnpublished), so a page published in between is refused there. A change held for approval runs the same
// way when it is approved, as the approver.
public static class SiteBuilderChanges
{
    public const string UnpublishedOnlyArgument = "unpublished_only";

    public const string UnpublishedOnlyDescription =
        "true to make the change at once, without approval, when it touches nothing customers see: the call checks it " +
        "first, and refuses without changing anything when a page it touches has a published version. Omit it (false) " +
        "to send the change for approval by a second person, which a change of published pages needs.";

    // The condition of [McpRequiresApproval]: every call needs approval unless it asks for unpublished pages only, which
    // the tool then checks itself.
    public static bool NeedsApproval(IReadOnlyDictionary<string, JsonElement> arguments) =>
        arguments == null || !arguments.TryGetValue(UnpublishedOnlyArgument, out var value) || value.ValueKind != JsonValueKind.True;

    // The dry run, and with dryRun false the write. A dry run that changes nothing writes nothing. checkAsync runs on the
    // dry run's result before anything is written: it pins the versions the write takes.
    public static async Task<(SiteBuilderChange Change, TResult Result)> RunAsync<TResult>(
        SiteBuilderChange change,
        Func<bool, Task<TResult>> run,
        Action<SiteBuilderOperationResult> ensureSucceeded,
        bool dryRun,
        bool unpublishedOnly = false,
        bool includeWarnings = false,
        Func<TResult, Task> checkAsync = null)
        where TResult : SiteBuilderOperationResult
    {
        var check = await run(true);
        ensureSucceeded(check);

        change.DryRun = true;
        change.AffectsPublished = check.AffectsPublished;
        change.AddChanges(check.Preview);
        if (includeWarnings)
        {
            // What a write of a page stored as a file does in git (the branches it merges) is told only here.
            foreach (var warning in check.Preview?.Warnings ?? [])
            {
                change.AddWarning(warning);
            }
        }

        if (check.Preview?.Objects.Count == 0)
        {
            change.AddWarning("The call changes nothing, so nothing was written.");
            return (change, check);
        }

        if (checkAsync != null)
        {
            await checkAsync(check);
        }

        if (unpublishedOnly && check.AffectsPublished)
        {
            if (dryRun)
            {
                change.AddWarning($"Customers would see this change, so with {UnpublishedOnlyArgument} the call is refused. Call it without " +
                    $"{UnpublishedOnlyArgument} to send it for approval.");
                return (change, check);
            }

            throw new McpException($"The change of {change.ObjectType} '{change.ObjectId}' touches a published page, so customers would see " +
                $"it; nothing was changed. Call the tool again without {UnpublishedOnlyArgument}: the change then waits for a second " +
                "person's approval.");
        }

        if (dryRun)
        {
            return (change, check);
        }

        var result = await run(false);
        if (unpublishedOnly)
        {
            EnsureStillUnpublished(change, result);
        }

        ensureSucceeded(result);
        change.DryRun = false;
        change.AffectsPublished = result.AffectsPublished;

        return (change, result);
    }

    // The operation refused the write under its lock: a page it touches was published after the dry run.
    private static void EnsureStillUnpublished(SiteBuilderChange change, SiteBuilderOperationResult result)
    {
        if (result.Status != SiteBuilderOperationStatus.Conflict || result.Error != SiteBuilderContentConflictException.PublishedPageMessage)
        {
            return;
        }

        // A Multilanguage Page saves its language versions one by one, each under its own lock: those before the refused one keep the change.
        var saved = change.ObjectType == ChangePreview.MultilanguagePage
            ? " What was saved before the published version (the Multilanguage Page's own values, other versions) may keep the change: read " +
              "the Multilanguage Page again with search_site_pages."
            : " Nothing was changed.";
        throw new McpException($"A page the change of {change.ObjectType} '{change.ObjectId}' touches was published after the check, so with " +
            $"{UnpublishedOnlyArgument} the write was refused.{saved} Call the tool again without {UnpublishedOnlyArgument}: the change then " +
            "waits for a second person's approval.");
    }

    // The operation's preview as field changes: those of the tool's own object under their names, those of other
    // objects (the language versions of a multilanguage page, say) under the object's name. Content changes are
    // shown section by section rather than as the whole document.
    public static void AddChanges(this McpChangePreview preview, SiteBuilderChangePreview source)
    {
        foreach (var item in source?.Objects ?? [])
        {
            var isOwn = item.ObjectId == preview.ObjectId && item.ObjectType == preview.ObjectType;
            var prefix = isOwn ? string.Empty : $"{item.ObjectType} '{item.ObjectId ?? "new"}': ";

            foreach (var field in item.Fields)
            {
                if (field.Name == ChangePreview.Content)
                {
                    AddContentChanges(preview, prefix, field);
                }
                else
                {
                    preview.AddChange(prefix + field.Name, field.From, field.To);
                }
            }
        }
    }

    // An operation refused: a sentence the user can act on, never the internals.
    public static void EnsureSucceeded(SiteBuilderOperationResult result, string subject, string forbiddenMessage)
    {
        switch (result.Status)
        {
            case SiteBuilderOperationStatus.Succeeded:
                return;
            case SiteBuilderOperationStatus.Forbidden:
                throw new McpForbiddenException(forbiddenMessage);
            case SiteBuilderOperationStatus.NotFound:
                throw new McpException($"{subject} was not found, or was deleted meanwhile. Call search_site_pages to find it.");
            case SiteBuilderOperationStatus.Conflict:
            case SiteBuilderOperationStatus.VersionRequired:
                throw new McpException($"{subject} changed since it was read. Read it again with search_site_pages and repeat the call.");
            case SiteBuilderOperationStatus.ConcurrentChange:
                throw new McpException($"{subject} was changed by another request at the same time, so this call changed nothing. " +
                    "Read it again with search_site_pages and repeat the call.");
            default:
                var details = result.Errors?.Count > 1 ? $" {string.Join(" ", result.Errors)}" : string.Empty;
                throw new McpException($"{subject} was not changed: {result.Error}{details}");
        }
    }

    private static void AddContentChanges(McpChangePreview preview, string prefix, SiteBuilderFieldChange field)
    {
        var before = PageDocumentEditor.Read(field.From);
        var after = PageDocumentEditor.Read(field.To);
        if (before == null || after == null)
        {
            preview.AddChange(prefix + "content", field.From == null ? null : "stored content", "new content (not a page document)");
            return;
        }

        foreach (var change in PageDocumentChanges.Compare(before, after))
        {
            preview.AddChange(prefix + change.Field, change.From, change.To);
        }
    }
}
