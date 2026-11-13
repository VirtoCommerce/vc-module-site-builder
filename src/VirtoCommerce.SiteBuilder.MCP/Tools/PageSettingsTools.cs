using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// The settings a page group keeps outside its content: name, address, language, audience and dates. Customers see a
// change of a published page as soon as it is saved, so such a change waits for a second person (see
// SiteBuilderChanges.NeedsApproval); a page that was never published changes at once with unpublished_only.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class PageSettingsTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IMultilanguagePageService multilanguagePageService,
    IGroupedPageOperationService groupedPageOperationService,
    IMultilanguagePageOperationService multilanguagePageOperationService)
{
    private const string UserGroupsField = "user_groups";
    private const string OrganizationField = "organization_id";
    private const string StartDateField = "start_date";
    private const string EndDateField = "end_date";
    private static readonly string[] ClearableFields = [UserGroupsField, OrganizationField, StartDateField, EndDateField];

    public static bool NeedsApproval(IReadOnlyDictionary<string, JsonElement> arguments) => SiteBuilderChanges.NeedsApproval(arguments);

    [McpServerTool(Name = "change_page_settings", ReadOnly = false, Destructive = true, Idempotent = true)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Change the settings of a database page: name, address (permalink), language, who sees it (visible, " +
        "user groups, organization) and the dates it is shown between. Pass page_id for a Landing Page or one language " +
        "version, or multilanguage_page_id for the values of a Multilanguage Page that its language versions inherit " +
        "(language is then its source language). Only the settings you pass change; clear removes a value. Customers " +
        "see a change of a published page at once, so it waits for approval; with unpublished_only a change of a page " +
        "that was never published is made at once. A start date makes a published page visible from that date; it does " +
        "not publish a draft. Not for the page's content, SEO or layout: use edit_page_draft. Call it with dryRun first " +
        "to show the change.")]
    public async Task<SiteBuilderChange> ChangePageSettings(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of a Landing Page or of a language version (search_site_pages). Pass it or multilanguage_page_id.")] string page_id = null,
        [Description("Id of a Multilanguage Page, to change the values its language versions inherit.")] string multilanguage_page_id = null,
        [Description("New name.")] string name = null,
        [Description("New address, for example '/spring-sale'.")] string permalink = null,
        [Description("New language code of the page, for example en-US; for a Multilanguage Page its source language, which must have a version.")] string language = null,
        [Description("false hides the page from every customer; true shows it to its audience.")] bool? visible = null,
        [Description("Customer groups that see the page; replaces the current ones. Use clear to show it to everyone again.")] string[] user_groups = null,
        [Description("Id of the only organization whose members see the page.")] string organization_id = null,
        [Description("The page is shown from this time (UTC).")] DateTime? start_date = null,
        [Description("The page is shown until this time (UTC).")] DateTime? end_date = null,
        [Description("Settings to remove: user_groups, organization_id, start_date, end_date.")] string[] clear = null,
        [Description(SiteBuilderChanges.UnpublishedOnlyDescription)] bool unpublished_only = false,
        [Description("true to check the change and return it, without saving it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var settings = new Settings(name, permalink, language, visible, user_groups, organization_id, PageLookup.ToUtc(start_date), PageLookup.ToUtc(end_date),
            clear ?? []);
        settings.Validate();

        if (!string.IsNullOrEmpty(page_id) == !string.IsNullOrEmpty(multilanguage_page_id))
        {
            throw new McpException("Pass either page_id or multilanguage_page_id.");
        }

        return string.IsNullOrEmpty(page_id)
            ? await ChangeMultilanguagePageAsync(context.User, multilanguage_page_id, settings, unpublished_only, dryRun, cancellationToken)
            : await ChangePageAsync(context.User, page_id, settings, unpublished_only, dryRun, cancellationToken);
    }

    private async Task<SiteBuilderChange> ChangePageAsync(ClaimsPrincipal user, string pageId, Settings settings, bool unpublishedOnly, bool dryRun,
        CancellationToken cancellationToken)
    {
        var group = await groupedPageService.LoadPageAsync(authorizationService, user, pageId);

        var model = group.CloneTyped();
        model.Name = settings.Name ?? model.Name;
        model.Permalink = settings.Permalink ?? model.Permalink;
        model.CultureName = settings.Language ?? model.CultureName;
        model.Visibility = settings.Visible ?? model.Visibility;
        model.UserGroups = settings.GetUserGroups(model.UserGroups);
        model.OrganizationId = settings.Get(OrganizationField, settings.OrganizationId, model.OrganizationId);
        model.StartDate = settings.Get(StartDateField, settings.StartDate, model.StartDate);
        model.EndDate = settings.Get(EndDateField, settings.EndDate, model.EndDate);

        var change = new SiteBuilderChange
        {
            Action = "change_page_settings",
            ObjectType = ChangePreview.Page,
            ObjectId = group.Id,
            ObjectName = group.Name,
            PageId = group.Id,
            MultilanguagePageId = group.MultilanguagePageId,
        };
        await SiteBuilderChanges.RunAsync(change,
            isDryRun => groupedPageOperationService.UpdateAsync(new GroupedPageSaveOperationRequest { Page = model, DryRun = isDryRun, ReportImpact = true }, user, cancellationToken),
            x => SiteBuilderChanges.EnsureSucceeded(x, $"Page '{group.Id}'", PageLookup.ForbiddenPage(group)),
            dryRun, unpublishedOnly);

        if (!string.IsNullOrEmpty(group.MultilanguagePageId))
        {
            change.AddWarning("This is a language version: a setting changed here overrides the Multilanguage Page's value, until reset_language_overrides resets it.");
        }

        return change;
    }

    private async Task<SiteBuilderChange> ChangeMultilanguagePageAsync(ClaimsPrincipal user, string multilanguagePageId, Settings settings, bool unpublishedOnly,
        bool dryRun, CancellationToken cancellationToken)
    {
        var root = await multilanguagePageService.LoadMultilanguagePageAsync(authorizationService, user, multilanguagePageId);

        var model = root.CloneTyped();
        model.Name = settings.Name ?? model.Name;
        model.Permalink = settings.Permalink ?? model.Permalink;
        model.DefaultCultureName = settings.Language ?? model.DefaultCultureName;
        model.Visibility = settings.Visible ?? model.Visibility;
        model.UserGroups = settings.GetUserGroups(model.UserGroups);
        model.OrganizationId = settings.Get(OrganizationField, settings.OrganizationId, model.OrganizationId);
        model.StartDate = settings.Get(StartDateField, settings.StartDate, model.StartDate);
        model.EndDate = settings.Get(EndDateField, settings.EndDate, model.EndDate);

        var change = new SiteBuilderChange
        {
            Action = "change_page_settings",
            ObjectType = ChangePreview.MultilanguagePage,
            ObjectId = root.Id,
            ObjectName = root.Name,
            MultilanguagePageId = root.Id,
        };
        await SiteBuilderChanges.RunAsync(change,
            isDryRun => multilanguagePageOperationService.UpdateAsync(new MultilanguagePageSaveOperationRequest { Page = model, DryRun = isDryRun, ReportImpact = true }, user,
                cancellationToken),
            x => SiteBuilderChanges.EnsureSucceeded(x, $"Multilanguage Page '{root.Id}'", PageLookup.ForbiddenMultilanguagePage(root)),
            dryRun, unpublishedOnly);

        return change;
    }

    private sealed record Settings(string Name, string Permalink, string Language, bool? Visible, string[] UserGroups, string OrganizationId,
        DateTime? StartDate, DateTime? EndDate, string[] Clear)
    {
        public void Validate()
        {
            var unknown = Clear.Where(x => !ClearableFields.Contains(x)).ToList();
            if (unknown.Count > 0)
            {
                throw new McpException($"clear takes {string.Join(", ", ClearableFields)}; not {string.Join(", ", unknown)}.");
            }

            var both = new List<string>();
            AddIfBoth(both, UserGroupsField, UserGroups);
            AddIfBoth(both, OrganizationField, OrganizationId);
            AddIfBoth(both, StartDateField, StartDate);
            AddIfBoth(both, EndDateField, EndDate);
            if (both.Count > 0)
            {
                throw new McpException($"Pass a value or clear it, not both: {string.Join(", ", both)}.");
            }

            if (Name == null && Permalink == null && Language == null && Visible == null && UserGroups == null && OrganizationId == null &&
                StartDate == null && EndDate == null && Clear.Length == 0)
            {
                throw new McpException("Pass at least one setting to change.");
            }
        }

        // Stored as one comma-separated text, as the page editor writes it.
        public string GetUserGroups(string current) =>
            Clear.Contains(UserGroupsField) ? null : UserGroups == null ? current : string.Join(",", UserGroups.Select(x => x.Trim()).Where(x => x.Length > 0));

        public T Get<T>(string field, T value, T current) => Clear.Contains(field) ? default : value ?? current;

        private void AddIfBoth(List<string> fields, string field, object value)
        {
            if (value != null && Clear.Contains(field))
            {
                fields.Add(field);
            }
        }
    }
}
