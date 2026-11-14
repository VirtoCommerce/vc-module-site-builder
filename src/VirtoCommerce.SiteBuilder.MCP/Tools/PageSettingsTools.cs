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
// change of a published page as soon as it is saved, so such a change waits for a second person (see NeedsApproval); a
// page that was never published changes at once with unpublished_only, which the operation checks again under the lock
// of its save. The dry run answers the version of the settings (expected_versions): the write takes them only at that
// version, so an approved change is refused once someone changed the page after the review. The write applies only the
// settings the call names, to the page as it is read right before it, never the model of an earlier read.
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
    private const string ExpectedVersionsDescription = "The expected_versions of the dry run, unchanged: the version of the settings the change " +
        "was reviewed at. Required without dryRun unless unpublished_only is set.";

    private static readonly string[] ClearableFields = [UserGroupsField, OrganizationField, StartDateField, EndDateField];

    // A call that touches published pages waits for a second person once it names the versions it reviewed; without
    // them it is a dry run or is refused by the tool, so nothing published changes without approval either way.
    public static bool NeedsApproval(IReadOnlyDictionary<string, JsonElement> arguments) =>
        SiteBuilderChanges.NeedsApproval(arguments) && ReviewedVersions.AreGiven(arguments);

    [McpServerTool(Name = "change_page_settings", ReadOnly = false, Destructive = true, Idempotent = true)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Change the settings of a database page: name, address (permalink), language, who sees it (visible, " +
        "user groups, organization) and the dates it is shown between. Pass page_id for a Landing Page or one language " +
        "version, or multilanguage_page_id for the values of a Multilanguage Page that its language versions inherit " +
        "(language is then its source language). Only the settings you pass change; clear removes a value. First call it " +
        "with dryRun: it shows the change and answers expected_versions. Customers see a change of a published page at " +
        "once, so call it then without dryRun and with those expected_versions: the change waits for approval and is " +
        "refused if someone changed the page in between. With unpublished_only a change of a page that was never " +
        "published is made at once (expected_versions optional) and refused if the page is published meanwhile. A start " +
        "date makes a published page visible from that date; it does not publish a draft. Not for the page's content, SEO " +
        "or layout: use edit_page_draft.")]
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
        [Description(ExpectedVersionsDescription)] Dictionary<string, string> expected_versions = null,
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

        var call = new SettingsCall(context.User, settings, unpublished_only, expected_versions, dryRun);
        return string.IsNullOrEmpty(page_id)
            ? await ChangeMultilanguagePageAsync(call, multilanguage_page_id, cancellationToken)
            : await ChangePageAsync(call, page_id, cancellationToken);
    }

    private async Task<SiteBuilderChange> ChangePageAsync(SettingsCall call, string pageId, CancellationToken cancellationToken)
    {
        var group = await groupedPageService.LoadPageAsync(authorizationService, call.User, pageId);

        var change = new SiteBuilderChange
        {
            Action = "change_page_settings",
            ObjectType = ChangePreview.Page,
            ObjectId = group.Id,
            ObjectName = group.Name,
            PageId = group.Id,
            MultilanguagePageId = group.MultilanguagePageId,
        };

        async Task<SiteBuilderOperationResult<GroupedSiteBuilderPage>> RunAsync(bool isDryRun)
        {
            var current = isDryRun ? group : await groupedPageService.GetByIdAsync(group.Id) ?? throw Deleted(change);
            return await groupedPageOperationService.UpdateAsync(new GroupedPageSaveOperationRequest
            {
                Page = call.Settings.ApplyTo(current),
                DryRun = isDryRun,
                ReportImpact = true,
                RequireUnpublished = call.UnpublishedOnly,
                ExpectedSettingsVersion = isDryRun ? null : ReviewedVersions.Of(change, group.Id),
            }, call.User, cancellationToken);
        }

        await SiteBuilderChanges.RunAsync(change, RunAsync,
            x => EnsureSucceeded(x, change, $"Page '{group.Id}'", PageLookup.ForbiddenPage(group)),
            call.DryRun, call.UnpublishedOnly,
            checkAsync: x => Pin(call, change, x));

        if (!string.IsNullOrEmpty(group.MultilanguagePageId))
        {
            change.AddWarning("This is a language version: a setting changed here overrides the Multilanguage Page's value, until reset_language_overrides resets it.");
        }

        return change;
    }

    private async Task<SiteBuilderChange> ChangeMultilanguagePageAsync(SettingsCall call, string multilanguagePageId, CancellationToken cancellationToken)
    {
        var root = await multilanguagePageService.LoadMultilanguagePageAsync(authorizationService, call.User, multilanguagePageId);

        var change = new SiteBuilderChange
        {
            Action = "change_page_settings",
            ObjectType = ChangePreview.MultilanguagePage,
            ObjectId = root.Id,
            ObjectName = root.Name,
            MultilanguagePageId = root.Id,
        };

        async Task<SiteBuilderOperationResult<MultilanguagePage>> RunAsync(bool isDryRun)
        {
            var current = isDryRun ? root : await multilanguagePageService.GetByIdAsync(root.Id) ?? throw Deleted(change);
            return await multilanguagePageOperationService.UpdateAsync(new MultilanguagePageSaveOperationRequest
            {
                Page = call.Settings.ApplyTo(current),
                DryRun = isDryRun,
                ReportImpact = true,
                RequireUnpublished = call.UnpublishedOnly,
                ExpectedSettingsVersion = isDryRun ? null : ReviewedVersions.Of(change, root.Id),
            }, call.User, cancellationToken);
        }

        await SiteBuilderChanges.RunAsync(change, RunAsync,
            x => EnsureSucceeded(x, change, $"Multilanguage Page '{root.Id}'", PageLookup.ForbiddenMultilanguagePage(root)),
            call.DryRun, call.UnpublishedOnly,
            checkAsync: x => Pin(call, change, x));

        return change;
    }

    // The version of the settings the dry run read: the write takes them only at it. A change waiting for approval
    // names it; an unpublished_only one may take its own dry run's.
    private static Task Pin(SettingsCall call, SiteBuilderChange change, SiteBuilderOperationResult check)
    {
        var version = check.Preview?.Objects.FirstOrDefault(x => x.ObjectType == change.ObjectType && x.ObjectId == change.ObjectId)?.ETag;
        ReviewedVersions.Pin(change, [new KeyValuePair<string, string>(change.ObjectId, version)], call.ExpectedVersions, call.DryRun,
            optional: call.UnpublishedOnly);

        return Task.CompletedTask;
    }

    private static void EnsureSucceeded(SiteBuilderOperationResult result, SiteBuilderChange change, string subject, string forbiddenMessage)
    {
        if (result.Status == SiteBuilderOperationStatus.Conflict && result.Error == SiteBuilderContentConflictException.SettingsChangedMessage)
        {
            throw ReviewedVersions.Changed(change);
        }

        SiteBuilderChanges.EnsureSucceeded(result, subject, forbiddenMessage);
    }

    private static McpException Deleted(SiteBuilderChange change) =>
        new($"{change.ObjectName} ('{change.ObjectId}') was deleted meanwhile, so nothing was changed. Call search_site_pages to find it.");

    private sealed record SettingsCall(ClaimsPrincipal User, Settings Settings, bool UnpublishedOnly, IDictionary<string, string> ExpectedVersions, bool DryRun);

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

        // A copy of the page with the settings the call names, and every other value as the page holds it.
        public GroupedSiteBuilderPage ApplyTo(GroupedSiteBuilderPage page)
        {
            var model = page.CloneTyped();
            model.Name = Name ?? model.Name;
            model.Permalink = Permalink ?? model.Permalink;
            model.CultureName = Language ?? model.CultureName;
            model.Visibility = Visible ?? model.Visibility;
            model.UserGroups = GetUserGroups(model.UserGroups);
            model.OrganizationId = Get(OrganizationField, OrganizationId, model.OrganizationId);
            model.StartDate = Get(StartDateField, StartDate, model.StartDate);
            model.EndDate = Get(EndDateField, EndDate, model.EndDate);

            return model;
        }

        public MultilanguagePage ApplyTo(MultilanguagePage page)
        {
            var model = page.CloneTyped();
            model.Name = Name ?? model.Name;
            model.Permalink = Permalink ?? model.Permalink;
            model.DefaultCultureName = Language ?? model.DefaultCultureName;
            model.Visibility = Visible ?? model.Visibility;
            model.UserGroups = GetUserGroups(model.UserGroups);
            model.OrganizationId = Get(OrganizationField, OrganizationId, model.OrganizationId);
            model.StartDate = Get(StartDateField, StartDate, model.StartDate);
            model.EndDate = Get(EndDateField, EndDate, model.EndDate);

            return model;
        }

        // Stored as one comma-separated text, as the page editor writes it.
        private string GetUserGroups(string current) =>
            Clear.Contains(UserGroupsField) ? null : UserGroups == null ? current : string.Join(",", UserGroups.Select(x => x.Trim()).Where(x => x.Length > 0));

        private T Get<T>(string field, T value, T current) => Clear.Contains(field) ? default : value ?? current;

        private void AddIfBoth(List<string> fields, string field, object value)
        {
            if (value != null && Clear.Contains(field))
            {
                fields.Add(field);
            }
        }
    }
}
