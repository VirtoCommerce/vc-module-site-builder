using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.MCP.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// A language link decides which address and audience a published version is served with, so changing one waits for approval.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class PageLanguageTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IMultilanguagePageService multilanguagePageService,
    IMultilanguagePageOperationService multilanguagePageOperationService)
{
    private const string PageKeyPrefix = "page:";
    private const string MultilanguagePageKeyPrefix = "multilanguage_page:";
    private const string ExpectedVersionsDescription = "The expected_versions of the dry run, unchanged: the versions of the pages ('page:<id>') " +
        "and of the Multilanguage Page ('multilanguage_page:<id>') the change was reviewed at. Required without dryRun unless unpublished_only is set.";
    private const string ReviewedFlow = " First call it with dryRun: it shows the change and answers expected_versions. Then call it without dryRun " +
        "and with those expected_versions: the change waits for approval and is refused if someone changed what it takes in between.";

    private static readonly Dictionary<string, string> InheritableFields = new(StringComparer.Ordinal)
    {
        ["permalink"] = MultilanguagePageInheritance.Permalink,
        ["visible"] = MultilanguagePageInheritance.Visibility,
        ["user_groups"] = MultilanguagePageInheritance.UserGroups,
        ["organization_id"] = MultilanguagePageInheritance.OrganizationId,
        ["start_date"] = MultilanguagePageInheritance.StartDate,
        ["end_date"] = MultilanguagePageInheritance.EndDate,
    };

    // Without expected_versions the tool refuses the write itself, so there is nothing to approve.
    public static bool NeedsApproval(IReadOnlyDictionary<string, JsonElement> arguments) =>
        SiteBuilderChanges.NeedsApproval(arguments) && ReviewedVersions.AreGiven(arguments);

    [McpServerTool(Name = "convert_page_to_multilanguage", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Create)]
    [Authorize(Update)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Turn a Landing Page into a Multilanguage Page: a new Multilanguage Page takes the page's name, address " +
        "and audience, and the page becomes its version in the page's language, served as before. Then add languages " +
        "with create_page (kind 'language_version'). A published page needs approval." + ReviewedFlow + " With unpublished_only " +
        "a page with no published version is converted at once (expected_versions optional).")]
    public async Task<SiteBuilderChange> ConvertPageToMultilanguage(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of the Landing Page (search_site_pages).")] string page_id,
        [Description(SiteBuilderChanges.UnpublishedOnlyDescription)] bool unpublished_only = false,
        [Description(ExpectedVersionsDescription)] Dictionary<string, string> expected_versions = null,
        [Description("true to return the change without making it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var group = await groupedPageService.LoadPageAsync(authorizationService, context.User, page_id);
        var change = NewPageChange("convert_page_to_multilanguage", group);

        var (_, result) = await SiteBuilderChanges.RunAsync(change,
            isDryRun => multilanguagePageOperationService.ConvertAsync(new MultilanguagePageVersionOperationRequest
            {
                GroupId = group.Id,
                DryRun = isDryRun,
                ReportImpact = true,
                RequireUnpublished = unpublished_only,
                ExpectedSettingsVersion = isDryRun ? null : ReviewedVersions.Of(change, PageKeyPrefix + group.Id),
            },
                context.User, cancellationToken),
            x => EnsureSucceeded(x, change, $"Page '{group.Id}'", PageLookup.ForbiddenPage(group)),
            dryRun, unpublished_only,
            checkAsync: x => Pin(change, x, expected_versions, dryRun, unpublished_only));

        change.MultilanguagePageId = result.Value?.Id;
        return change;
    }

    [McpServerTool(Name = "link_page_language", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Make an existing Landing Page the version of a Multilanguage Page in the page's language. The settings " +
        "in which the page differs from the Multilanguage Page stay as the page's own, so the page is served as before. " +
        "A change touching a published page needs approval." + ReviewedFlow + " With unpublished_only a change of pages with " +
        "no published version is made at once (expected_versions optional).")]
    public async Task<SiteBuilderChange> LinkPageLanguage(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of the Landing Page to link (search_site_pages).")] string page_id,
        [Description("Id of the Multilanguage Page to link it to.")] string multilanguage_page_id,
        [Description(SiteBuilderChanges.UnpublishedOnlyDescription)] bool unpublished_only = false,
        [Description(ExpectedVersionsDescription)] Dictionary<string, string> expected_versions = null,
        [Description("true to return the change without making it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var group = await groupedPageService.LoadPageAsync(authorizationService, context.User, page_id);
        var root = await multilanguagePageService.LoadMultilanguagePageAsync(authorizationService, context.User, multilanguage_page_id);
        var change = NewPageChange("link_page_language", group);
        change.MultilanguagePageId = root.Id;

        await SiteBuilderChanges.RunAsync(change,
            isDryRun => multilanguagePageOperationService.LinkAsync(new MultilanguagePageLinkOperationRequest
            {
                MultilanguagePageId = root.Id,
                GroupId = group.Id,
                DryRun = isDryRun,
                ReportImpact = true,
                RequireUnpublished = unpublished_only,
                ExpectedMultilanguagePageVersion = isDryRun ? null : ReviewedVersions.Of(change, MultilanguagePageKeyPrefix + root.Id),
                ExpectedSettingsVersion = isDryRun ? null : ReviewedVersions.Of(change, PageKeyPrefix + group.Id),
            }, context.User, cancellationToken),
            x => EnsureSucceeded(x, change, $"Page '{group.Id}'", $"{PageLookup.ForbiddenPage(group)} {PageLookup.ForbiddenMultilanguagePage(root)}"),
            dryRun, unpublished_only,
            checkAsync: x => Pin(change, x, expected_versions, dryRun, unpublished_only));

        return change;
    }

    [McpServerTool(Name = "unlink_page_language", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Turn a language version of a Multilanguage Page back into a Landing Page with its current settings. A " +
        "published version needs approval." + ReviewedFlow + " With unpublished_only a version with no published page is " +
        "unlinked at once (expected_versions optional).")]
    public async Task<SiteBuilderChange> UnlinkPageLanguage(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of the language version (search_site_pages).")] string page_id,
        [Description(SiteBuilderChanges.UnpublishedOnlyDescription)] bool unpublished_only = false,
        [Description(ExpectedVersionsDescription)] Dictionary<string, string> expected_versions = null,
        [Description("true to return the change without making it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var group = await groupedPageService.LoadPageAsync(authorizationService, context.User, page_id);
        if (string.IsNullOrEmpty(group.MultilanguagePageId))
        {
            throw new McpException($"Page '{group.Id}' is a Landing Page already, not a language version.");
        }

        var change = NewPageChange("unlink_page_language", group);
        await SiteBuilderChanges.RunAsync(change,
            isDryRun => multilanguagePageOperationService.UnlinkAsync(new MultilanguagePageVersionOperationRequest
            {
                GroupId = group.Id,
                DryRun = isDryRun,
                ReportImpact = true,
                RequireUnpublished = unpublished_only,
                ExpectedSettingsVersion = isDryRun ? null : ReviewedVersions.Of(change, PageKeyPrefix + group.Id),
            },
                context.User, cancellationToken),
            x => EnsureSucceeded(x, change, $"Page '{group.Id}'", PageLookup.ForbiddenPage(group)),
            dryRun, unpublished_only,
            checkAsync: x => Pin(change, x, expected_versions, dryRun, unpublished_only));

        return change;
    }

    // Not a background job: one save per language version, and a page has at most one per store language.
    [McpServerTool(Name = "reset_language_overrides", ReadOnly = false, Destructive = true, Idempotent = true)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Give every language version of a Multilanguage Page the Multilanguage Page's own value of the chosen " +
        "settings, dropping the values the versions set for themselves. Returns each version's changes. Changing published " +
        "versions needs approval." + ReviewedFlow + " With unpublished_only a change of versions with no published page is " +
        "made at once (expected_versions optional).")]
    public async Task<SiteBuilderChange> ResetLanguageOverrides(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of the Multilanguage Page (search_site_pages).")] string multilanguage_page_id,
        [Description("Settings to reset: permalink, visible, user_groups, organization_id, start_date, end_date.")] string[] fields,
        [Description(SiteBuilderChanges.UnpublishedOnlyDescription)] bool unpublished_only = false,
        [Description(ExpectedVersionsDescription)] Dictionary<string, string> expected_versions = null,
        [Description("true to return the changes without making them.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var unknown = (fields ?? []).Where(x => !InheritableFields.ContainsKey(x)).ToList();
        if (fields == null || fields.Length == 0 || unknown.Count > 0)
        {
            throw new McpException($"fields takes one or more of {string.Join(", ", InheritableFields.Keys)}" +
                (unknown.Count > 0 ? $"; not {string.Join(", ", unknown)}." : "."));
        }

        var root = await multilanguagePageService.LoadMultilanguagePageAsync(authorizationService, context.User, multilanguage_page_id);
        var change = new SiteBuilderChange
        {
            Action = "reset_language_overrides",
            ObjectType = ChangePreview.MultilanguagePage,
            ObjectId = root.Id,
            ObjectName = root.Name,
            MultilanguagePageId = root.Id,
        };

        var (_, result) = await SiteBuilderChanges.RunAsync(change,
            isDryRun => multilanguagePageOperationService.ResetOverridesAsync(new MultilanguagePageResetOverridesOperationRequest
            {
                MultilanguagePageId = root.Id,
                Fields = fields.Select(x => InheritableFields[x]).Distinct().ToList(),
                DryRun = isDryRun,
                ReportImpact = true,
                RequireUnpublished = unpublished_only,
                ExpectedMultilanguagePageVersion = isDryRun ? null : ReviewedVersions.Of(change, MultilanguagePageKeyPrefix + root.Id),
                ExpectedSettingsVersions = isDryRun ? null : PageVersions(change),
            }, context.User, cancellationToken),
            x => EnsureSucceeded(x, change, $"Multilanguage Page '{root.Id}'", PageLookup.ForbiddenMultilanguagePage(root)),
            dryRun, unpublished_only,
            checkAsync: x => Pin(change, x, expected_versions, dryRun, unpublished_only));

        if (!dryRun && result.Value?.Count > 0)
        {
            change.AddWarning($"Reset in: {string.Join(", ", result.Value)}.");
        }

        return change;
    }

    // A change that needs no approval may take the versions of its own dry run.
    private static Task Pin(SiteBuilderChange change, SiteBuilderOperationResult check, IDictionary<string, string> expectedVersions, bool dryRun,
        bool unpublishedOnly)
    {
        var current = (check.Preview?.Objects ?? [])
            .Where(x => x.ObjectId != null)
            .Select(x => new KeyValuePair<string, string>(
                (x.ObjectType == ChangePreview.MultilanguagePage ? MultilanguagePageKeyPrefix : PageKeyPrefix) + x.ObjectId, x.ETag));
        ReviewedVersions.Pin(change, current, expectedVersions, dryRun, optional: unpublishedOnly);

        return Task.CompletedTask;
    }

    private static Dictionary<string, string> PageVersions(SiteBuilderChange change) =>
        (change.ExpectedVersions ?? new Dictionary<string, string>())
            .Where(x => x.Key.StartsWith(PageKeyPrefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.Key[PageKeyPrefix.Length..], x => x.Value, StringComparer.OrdinalIgnoreCase);

    private static void EnsureSucceeded(SiteBuilderOperationResult result, SiteBuilderChange change, string subject, string forbiddenMessage)
    {
        if (result.Status == SiteBuilderOperationStatus.Conflict && result.Error == SiteBuilderContentConflictException.SettingsChangedMessage)
        {
            // A reset saves the language versions one by one, each under its own lock: those before one changed meanwhile keep the reset.
            throw change.ObjectType == ChangePreview.MultilanguagePage
                ? new McpException($"Multilanguage Page '{change.ObjectId}' or a language version the reset takes changed since the dry run that " +
                    $"gave {ReviewedVersions.Argument}, so the reset was refused. If a version changed while the versions were being saved, those " +
                    $"saved before it keep the reset. Call {change.Action} with dryRun again, show the user what it would do now, and send the new " +
                    $"{ReviewedVersions.Argument}.")
                : ReviewedVersions.Changed(change);
        }

        SiteBuilderChanges.EnsureSucceeded(result, subject, forbiddenMessage);
    }

    private static SiteBuilderChange NewPageChange(string action, GroupedSiteBuilderPage group) => new()
    {
        Action = action,
        ObjectType = ChangePreview.Page,
        ObjectId = group.Id,
        ObjectName = group.Name,
        PageId = group.Id,
        MultilanguagePageId = group.MultilanguagePageId,
    };
}
