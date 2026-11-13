using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// A version restored overwrites the drafts it names, and so the unpublished work in them: the user confirms it in the
// chat, and a client that cannot ask does not restore. Customers see nothing of it until the drafts are published. The
// call names the versions of the drafts the agent read (expected_versions), so a draft saved by someone meanwhile is not
// overwritten: the restore of database pages and theme files checks them under its lock; a page stored as a file in git
// keeps no version of its own, and its hash is checked against the restore's own dry run just before the commit.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class VersionRestoreTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IContentHistoryOperationService contentHistoryOperationService,
    IFilePageOperationService filePageOperationService)
{
    // A commit is named by at least its short form, as the history of a page stored as a file shows it.
    private const int MinCommitLength = 7;

    [McpServerTool(Name = "restore_version", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [McpRequiresConfirmation(Fallback = McpConfirmationFallback.Refuse)]
    [Description("Bring back an earlier version of a page or of theme files as their draft, to edit or publish from there: " +
        "the drafts they have now are overwritten, and customers see nothing until the draft is published (publish_page, " +
        "publish_theme). Name the version with version_id from list_versions and the items with page_id, theme_files or both " +
        "(a database page and theme files of one version are restored together). A page stored as a file in git is named by " +
        "its path with store_id, and version_id is the commit of the version. First call it with dryRun: it shows what each " +
        "draft would lose and answers expected_versions, the versions of the drafts now. Then call it without dryRun and with " +
        "those expected_versions (or the version_token search_site_pages or search_templates gave for each item); a draft " +
        "changed since is not overwritten. Theme files need builder:templates for templates and rules, builder:theme for the " +
        "others. The user confirms the call in the chat; a client that cannot ask refuses it.")]
    public async Task<VersionRestore> RestoreVersion(
        RequestContext<CallToolRequestParams> context,
        [Description("Store id.")] string store_id,
        [Description("version_id of list_versions; for a page stored as a file in git, the commit of the version.")] string version_id,
        [Description("Id of a database page, or the path of a page stored as a file ('pages/about.page').")] string page_id = null,
        [Description("Theme files by their path with the theme folder, as list_versions names them, for example 'default/templates/product.json'.")] string[] theme_files = null,
        [Description("The versions of the drafts the restore overwrites, by item: 'page:<page_id>' or 'theme:<path>', as the dry run answers " +
            "them. Required to restore.")] Dictionary<string, string> expected_versions = null,
        [Description("true to show what the restore would overwrite, without restoring.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(store_id) || string.IsNullOrWhiteSpace(version_id))
        {
            throw new McpException("Pass store_id and version_id; list_versions names the versions of a page or a theme file.");
        }

        var storeId = store_id.Trim();
        var versionId = version_id.Trim();

        // The store first, as the operations check it, so an answer about the arguments does not tell which stores exist.
        await authorizationService.EnsureAllowedAsync(context.User, new SiteBuilderPageSearchCriteria { StoreId = storeId }, storeId);

        var themeFiles = (theme_files ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().TrimStart('/')).Distinct().ToList();
        if (PageLookup.IsFilePagePath(page_id))
        {
            if (themeFiles.Count > 0)
            {
                throw new McpException("A page stored as a file is restored from its own history, apart from the theme: call restore_version once for each.");
            }

            return await RestoreFilePageAsync(context.User, FilePages.Parse(storeId, page_id), versionId, expected_versions, dryRun, cancellationToken);
        }

        var items = themeFiles.Select(x => HistoryItem.ForThemeFile(x).ToString()).ToList();
        if (!string.IsNullOrWhiteSpace(page_id))
        {
            var group = await groupedPageService.LoadPageAsync(authorizationService, context.User, page_id.Trim());
            if (!storeId.EqualsIgnoreCase(group.StoreId))
            {
                throw new McpException($"Page '{group.Id}' is not a page of store '{storeId}'.");
            }

            items.Insert(0, new HistoryItem(HistoryItemType.Page, group.Id).ToString());
        }

        if (items.Count == 0)
        {
            throw new McpException("Pass page_id, theme_files or both: the items of the version to restore.");
        }

        return await RestoreHistoryAsync(context.User, storeId, versionId, items, expected_versions, dryRun, cancellationToken);
    }

    private async Task<VersionRestore> RestoreHistoryAsync(ClaimsPrincipal user, string storeId, string versionId, IList<string> items,
        Dictionary<string, string> expectedVersions, bool dryRun, CancellationToken cancellationToken)
    {
        ContentHistoryRestoreOperationRequest Request(bool isDryRun, IDictionary<string, string> versions) => new()
        {
            StoreId = storeId,
            VersionId = versionId,
            Items = items,
            SaveId = Guid.NewGuid().ToString("N"),
            DryRun = isDryRun,
            ExpectedVersions = versions,
        };

        var check = await contentHistoryOperationService.RestoreAsync(Request(true, null), user, cancellationToken);
        EnsureRestored(check, storeId, versionId);

        var answer = NewAnswer(storeId, versionId, items);
        foreach (var item in check.Preview?.Objects ?? [])
        {
            var key = item.ObjectType == ChangePreview.Page
                ? new HistoryItem(HistoryItemType.Page, item.ObjectId).ToString()
                : HistoryItem.ForThemeFile(item.ObjectId).ToString();
            answer.ExpectedVersions[key] = item.ETag;
            AddContentChanges(answer, items.Count > 1 ? key : null, item);
        }

        var expected = Normalize(expectedVersions);
        EnsureExpected(expected, answer.ExpectedVersions, dryRun);
        if (dryRun)
        {
            return answer;
        }

        var result = await contentHistoryOperationService.RestoreAsync(Request(false, expected), user, cancellationToken);
        EnsureRestored(result, storeId, versionId);
        answer.DryRun = false;

        return answer;
    }

    private async Task<VersionRestore> RestoreFilePageAsync(ClaimsPrincipal user, FilePage page, string versionId,
        Dictionary<string, string> expectedVersions, bool dryRun, CancellationToken cancellationToken)
    {
        if (versionId.Length < MinCommitLength)
        {
            throw new McpException($"'{versionId}' is not a commit: name the version of a page stored as a file by its commit, at least {MinCommitLength} characters.");
        }

        FilePageRestoreVersionOperationRequest Request(bool isDryRun) => new()
        {
            StoreId = page.StoreId,
            ContentType = page.ContentType,
            Path = page.Path,
            Sha = versionId,
            DryRun = isDryRun,
        };

        var check = await FilePages.RunAsync(() => filePageOperationService.RestoreVersionAsync(Request(true), user, cancellationToken));
        EnsureFilePageRestored(check, page, versionId);

        var answer = NewAnswer(page.StoreId, versionId, [page.VersionKey]);
        answer.PageId = page.PageId;
        foreach (var item in check.Preview?.Objects ?? [])
        {
            answer.ExpectedVersions[page.VersionKey] = FilePages.VersionOf(item.Fields.FirstOrDefault(x => x.Name == ChangePreview.Content)?.From);
            AddContentChanges(answer, prefix: null, item);
        }

        foreach (var warning in check.Preview?.Warnings ?? [])
        {
            answer.AddWarning(warning);
        }

        EnsureExpected(Normalize(expectedVersions), answer.ExpectedVersions, dryRun);
        if (dryRun)
        {
            return answer;
        }

        var result = await FilePages.RunAsync(() => filePageOperationService.RestoreVersionAsync(Request(false), user, cancellationToken));
        EnsureFilePageRestored(result, page, versionId);
        answer.DryRun = false;
        answer.Commit = result.Value?.CommitSha;

        return answer;
    }

    // The versions the call names must be those of the drafts now, for every item: a dry run tells the agent at once, the
    // call itself is refused before anything is written.
    private static void EnsureExpected(IDictionary<string, string> expected, IDictionary<string, string> current, bool dryRun)
    {
        if (expected == null)
        {
            if (dryRun)
            {
                return;
            }

            throw new McpException("Nothing was restored: pass expected_versions, the versions of the drafts the restore overwrites, as the " +
                "dry run answers them, so that a draft saved meanwhile is not lost. Call restore_version with dryRun first.");
        }

        var missing = current.Keys.Where(x => !expected.ContainsKey(x)).ToList();
        if (missing.Count > 0)
        {
            throw new McpException($"expected_versions has no version of {string.Join(", ", missing)}. Pass the expected_versions of the dry run unchanged. Nothing was restored.");
        }

        var changed = current.Where(x => !string.Equals(expected[x.Key], x.Value, StringComparison.Ordinal)).Select(x => x.Key).ToList();
        if (changed.Count > 0)
        {
            throw new McpException($"The draft of {string.Join(", ", changed)} changed since its version was read, so nothing was restored. Read " +
                "it again (or call restore_version with dryRun), show the user what the restore would overwrite now, and send the new expected_versions.");
        }
    }

    private static Dictionary<string, string> Normalize(Dictionary<string, string> expectedVersions) =>
        expectedVersions?.ToDictionary(
            x => HistoryItem.TryParse(x.Key?.Trim(), out var item) && item.Type == HistoryItemType.Theme ? HistoryItem.ForThemeFile(item.Key).ToString() : x.Key?.Trim() ?? string.Empty,
            x => x.Value?.Trim(),
            StringComparer.Ordinal);

    // The drafts' content against the version's, section by section for pages and templates, by value for the other files.
    private static void AddContentChanges(VersionRestore answer, string prefix, SiteBuilderObjectChange item)
    {
        var content = item.Fields.FirstOrDefault(x => x.Name == ChangePreview.Content);
        var label = prefix == null ? string.Empty : $"{prefix}: ";
        var changes = item.ObjectType == ChangePreview.ThemeFile
            ? ThemeFileEdits.Compare(item.ObjectId, content?.From, content?.To)
            : CompareDocuments(content?.From, content?.To);

        if (changes == null)
        {
            answer.AddChange(label + "content", "draft", "content of the version (not a page document)");
            return;
        }

        if (changes.Count == 0)
        {
            answer.AddWarning($"{(prefix ?? item.ObjectId)}: the draft already holds the version; the restore changes nothing in it.");
        }

        foreach (var change in changes)
        {
            answer.AddChange(label + change.Field, change.From, change.To);
        }
    }

    private static IList<PageDocumentChange> CompareDocuments(string before, string after) =>
        PageDocumentEditor.Read(before) is { } from && PageDocumentEditor.Read(after) is { } to ? PageDocumentChanges.Compare(from, to) : null;

    private static VersionRestore NewAnswer(string storeId, string versionId, IList<string> items)
    {
        var answer = new VersionRestore
        {
            Action = "restore_version",
            ObjectType = "version",
            ObjectId = versionId,
            ObjectName = $"Version {versionId} of {string.Join(", ", items)}",
            StoreId = storeId,
            VersionId = versionId,
            DryRun = true,
        };
        answer.AddWarning("The version becomes the draft: customers see it only once it is published (publish_page, publish_theme).");

        return answer;
    }

    private static void EnsureRestored(SiteBuilderOperationResult result, string storeId, string versionId)
    {
        switch (result.Status)
        {
            case SiteBuilderOperationStatus.Succeeded:
                return;
            case SiteBuilderOperationStatus.Forbidden:
                throw new McpForbiddenException($"You may not restore these items in store '{storeId}': it needs builder:update, builder:templates " +
                    "for templates and rules and builder:theme for the other theme files, and builder:read there (with " +
                    "builder:shared-components:read when the page places Shared Components).");
            case SiteBuilderOperationStatus.NotFound:
                throw new McpException($"Version '{versionId}' of these items was not found in store '{storeId}': list_versions names the versions " +
                    "and the items each one changed.");
            case SiteBuilderOperationStatus.Conflict:
            case SiteBuilderOperationStatus.VersionRequired:
                throw new McpException("A draft changed since its version was read, so nothing was restored. Call restore_version with dryRun " +
                    "again, show the user what it would overwrite now, and send the new expected_versions.");
            default:
                var details = result.Errors?.Count > 0 ? $" {string.Join("; ", result.Errors)}" : string.Empty;
                throw new McpException($"Version '{versionId}' was not restored: {result.Error}{details}");
        }
    }

    private static void EnsureFilePageRestored(SiteBuilderOperationResult result, FilePage page, string versionId)
    {
        if (result.Status == SiteBuilderOperationStatus.NotFound)
        {
            throw new McpException($"Page '{page.PageId}' has no version '{versionId}' in store '{page.StoreId}': the store keeps versions of pages " +
                "stored as files only in git, and the commit must hold the page.");
        }

        FilePages.EnsureSucceeded(result, page, "restore a version of");
    }
}

public sealed class VersionRestore : McpChangePreview
{
    [JsonPropertyName("store_id")]
    public string StoreId { get; set; }

    [JsonPropertyName("version_id")]
    public string VersionId { get; set; }

    // A page stored as a file.
    [JsonPropertyName("page_id")]
    public string PageId { get; set; }

    // The versions of the drafts now, by item: pass them unchanged to restore.
    [JsonPropertyName("expected_versions")]
    public IDictionary<string, string> ExpectedVersions { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    // A page stored as a file in git: the commit on your draft branch that holds the restored version.
    [JsonPropertyName("commit")]
    public string Commit { get; set; }
}
