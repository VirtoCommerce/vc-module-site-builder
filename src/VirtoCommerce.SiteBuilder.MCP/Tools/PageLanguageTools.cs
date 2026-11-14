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
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// The language links of Multilanguage Pages. A link decides which address and audience a published version is served
// with, so a change that touches a published version waits for a second person (SiteBuilderChanges.NeedsApproval);
// with unpublished_only a change of pages that were never published is made at once.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class PageLanguageTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IMultilanguagePageService multilanguagePageService,
    IMultilanguagePageOperationService multilanguagePageOperationService)
{
    // The agent's names of MultilanguagePageInheritance.InheritableFields.
    private static readonly Dictionary<string, string> InheritableFields = new(StringComparer.Ordinal)
    {
        ["permalink"] = MultilanguagePageInheritance.Permalink,
        ["visible"] = MultilanguagePageInheritance.Visibility,
        ["user_groups"] = MultilanguagePageInheritance.UserGroups,
        ["organization_id"] = MultilanguagePageInheritance.OrganizationId,
        ["start_date"] = MultilanguagePageInheritance.StartDate,
        ["end_date"] = MultilanguagePageInheritance.EndDate,
    };

    public static bool NeedsApproval(IReadOnlyDictionary<string, JsonElement> arguments) => SiteBuilderChanges.NeedsApproval(arguments);

    [McpServerTool(Name = "convert_page_to_multilanguage", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Create)]
    [Authorize(Update)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Turn a Landing Page into a Multilanguage Page: a new Multilanguage Page takes the page's name, address " +
        "and audience, and the page becomes its version in the page's language, served as before. Then add languages " +
        "with create_page (kind 'language_version'). A published page needs approval; with unpublished_only a page that " +
        "was never published is converted at once. Call it with dryRun first to show the change.")]
    public async Task<SiteBuilderChange> ConvertPageToMultilanguage(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of the Landing Page (search_site_pages).")] string page_id,
        [Description(SiteBuilderChanges.UnpublishedOnlyDescription)] bool unpublished_only = false,
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
            },
                context.User, cancellationToken),
            x => SiteBuilderChanges.EnsureSucceeded(x, $"Page '{group.Id}'", PageLookup.ForbiddenPage(group)),
            dryRun, unpublished_only);

        change.MultilanguagePageId = result.Value?.Id;
        return change;
    }

    [McpServerTool(Name = "link_page_language", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Make an existing Landing Page the version of a Multilanguage Page in the page's language. The settings " +
        "in which the page differs from the Multilanguage Page stay as the page's own, so the page is served as before. " +
        "A change touching a published page needs approval; with unpublished_only a change of pages that were never " +
        "published is made at once. Call it with dryRun first to show the change.")]
    public async Task<SiteBuilderChange> LinkPageLanguage(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of the Landing Page to link (search_site_pages).")] string page_id,
        [Description("Id of the Multilanguage Page to link it to.")] string multilanguage_page_id,
        [Description(SiteBuilderChanges.UnpublishedOnlyDescription)] bool unpublished_only = false,
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
            }, context.User, cancellationToken),
            x => SiteBuilderChanges.EnsureSucceeded(x, $"Page '{group.Id}'", $"{PageLookup.ForbiddenPage(group)} {PageLookup.ForbiddenMultilanguagePage(root)}"),
            dryRun, unpublished_only);

        return change;
    }

    [McpServerTool(Name = "unlink_page_language", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Turn a language version of a Multilanguage Page back into a Landing Page with its current settings. A " +
        "published version needs approval; with unpublished_only a version that was never published is unlinked at " +
        "once. Call it with dryRun first to show the change.")]
    public async Task<SiteBuilderChange> UnlinkPageLanguage(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of the language version (search_site_pages).")] string page_id,
        [Description(SiteBuilderChanges.UnpublishedOnlyDescription)] bool unpublished_only = false,
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
            },
                context.User, cancellationToken),
            x => SiteBuilderChanges.EnsureSucceeded(x, $"Page '{group.Id}'", PageLookup.ForbiddenPage(group)),
            dryRun, unpublished_only);

        return change;
    }

    // Runs at once: the operation saves the versions in one call, one save per version, and a page has a version per
    // store language only.
    [McpServerTool(Name = "reset_language_overrides", ReadOnly = false, Destructive = true, Idempotent = true)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Give every language version of a Multilanguage Page the Multilanguage Page's own value of the chosen " +
        "settings, dropping the values the versions set for themselves. Changing published versions needs approval; " +
        "with unpublished_only a change of versions that were never published is made at once. Returns each version's " +
        "changes. Call it with dryRun first to show them.")]
    public async Task<SiteBuilderChange> ResetLanguageOverrides(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of the Multilanguage Page (search_site_pages).")] string multilanguage_page_id,
        [Description("Settings to reset: permalink, visible, user_groups, organization_id, start_date, end_date.")] string[] fields,
        [Description(SiteBuilderChanges.UnpublishedOnlyDescription)] bool unpublished_only = false,
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
            }, context.User, cancellationToken),
            x => SiteBuilderChanges.EnsureSucceeded(x, $"Multilanguage Page '{root.Id}'", PageLookup.ForbiddenMultilanguagePage(root)),
            dryRun, unpublished_only);

        if (!dryRun && result.Value?.Count > 0)
        {
            change.AddWarning($"Reset in: {string.Join(", ", result.Value)}.");
        }

        return change;
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
