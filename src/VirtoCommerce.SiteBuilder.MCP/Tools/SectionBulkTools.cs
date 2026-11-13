using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.MCP.Jobs;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// One change of a section on every page that has it: a banner replaced on all landing pages. Only drafts change, so no
// second person checks it; the pages are fixed when the tool is called and changed by a background job.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class SectionBulkTools(
    IAuthorizationService authorizationService,
    ISiteBuilderSectionScanner sectionScanner,
    PageDraftJobRunner runner,
    IMcpJobDispatcher jobDispatcher,
    Func<UserManager<ApplicationUser>> userManagerFactory)
{
    // A dry run reads this many pages; a real run reads every page of the filters, at most this many, so that the set
    // it fixes is the whole set.
    private const int MaxPreviewScan = 200;
    private const int MaxPages = 1000;
    private const int MaxPreviewPages = 10;

    [McpServerTool(Name = "update_sections_bulk", ReadOnly = false, Destructive = false, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [Description("Make one change to every section of a type, optionally only those whose setting matches a condition, " +
        "on the drafts of all database pages the filters select: set settings (texts, images, links), hide, show or remove " +
        "the section. Customers see the change only when each draft is published. Call it with dryRun first: it scans " +
        "up to 200 pages and shows the change on the first pages, with how many pages were scanned and matched. The real " +
        "call fixes the pages and their versions and starts a background job (follow it with get_job_status); a page " +
        "saved by someone after the call is skipped, not overwritten. Sections that a placed Shared Component brings are " +
        "not changed: the component is changed on its own. For one page use edit_page_draft.")]
    public async Task<object> UpdateSectionsBulk(
        RequestContext<CallToolRequestParams> context,
        [Description("Type of the sections to change, for example 'hero-banner'.")] string section_type,
        [Description("set_section_settings (with settings and optional settings_language), hide_section, show_section or remove_section.")] string change,
        [Description("Store ids. Omit them for every store you edit pages of.")] string[] store_ids = null,
        [Description("Text in the page name or address.")] string keyword = null,
        [Description("Draft, Published or Archived; several, comma-separated.")] string status = null,
        [Description("Language code, for example en-US: only the language versions in it.")] string language = null,
        [Description("Landing or Multilanguage. Omit it for both.")] string page_type = null,
        [Description("A setting of the section to compare: a name or a dotted path such as 'title' or 'button.url'; " +
            "'blocks.*.text' compares the text of any block.")] string setting_path = null,
        [Description("With setting_path: the exact value the setting has.")] string setting_equals = null,
        [Description("With setting_path: text the setting contains, ignoring case.")] string setting_contains = null,
        [Description("With setting_path: compare only this language's text of a setting that holds a text per language.")] string setting_language = null,
        [Description("set_section_settings: the values to set, as in edit_page_draft: a key is a setting name or a " +
            "dotted path ('title', 'button.url', 'blocks.b1.text'); null removes the setting.")] Dictionary<string, JsonNode> settings = null,
        [Description("set_section_settings: the language whose text the values set, for settings that hold a text per language.")] string settings_language = null,
        [Description("true to scan and show the change without starting it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var storeIds = await GetStoreIdsAsync(context.User, store_ids);

        if (string.IsNullOrWhiteSpace(section_type))
        {
            throw new McpException("Pass section_type: the type of the sections to change.");
        }

        var op = SectionBulkChanges.Parse(change, settings, settings_language);
        var scan = await sectionScanner.ScanPagesAsync(new SiteBuilderSectionScanCriteria
        {
            Pages = new SiteBuilderPageSearchCriteria
            {
                StoreIds = storeIds,
                Keyword = keyword,
                Statuses = SitePageFilters.ParseStatuses(status),
                LanguageCode = language,
                PageType = SitePageFilters.ParsePageType(page_type),
            },
            MaxPages = dryRun ? MaxPreviewScan : MaxPages,
            SectionType = section_type.Trim(),
            Condition = SitePageFilters.ParseCondition(setting_path, setting_equals, setting_contains, setting_language),
            SharedComponentStoreIds = await GetSharedComponentStoreIdsAsync(context.User, storeIds),
        }, cancellationToken);

        if (!dryRun && !scan.IsComplete)
        {
            throw new McpException($"The filters select {scan.TotalPageCount} pages, more than the {MaxPages} one call changes. Narrow them " +
                "with store_ids, language, status, page_type or keyword, and make the change part by part. Nothing was changed.");
        }

        var targets = scan.Pages
            .Select(x => new UpdateSectionsBulkJobPage
            {
                PageId = x.Page.Id,
                StoreId = x.Page.StoreId,
                VersionToken = x.ETag,
                SectionIds = x.Sections.Where(s => s.SharedComponentId == null).Select(s => s.SectionId).ToList(),
            })
            .Where(x => x.SectionIds.Count > 0)
            .ToList();

        var preview = await PreviewAsync(context.User, scan, targets, section_type.Trim(), op, settings, settings_language, dryRun, cancellationToken);
        if (dryRun || targets.Count == 0)
        {
            return preview;
        }

        var payload = new UpdateSectionsBulkJobPayload
        {
            UserId = context.User.GetUserId(),
            SectionType = section_type.Trim(),
            Change = op,
            SettingsJson = SectionBulkChanges.WriteSettings(settings),
            SettingsLanguage = settings_language,
            Pages = targets,
        };

        return await jobDispatcher.EnqueueAsync<UpdateSectionsBulkJob>(context, payload, new McpJobRequest
        {
            Title = $"{op} on {preview.TargetSectionCount} {section_type.Trim()} sections of {targets.Count} pages",
            TotalCount = targets.Count,
            Preview = preview.Pages,
        }, "make the change page by page with edit_page_draft.");
    }

    // The named stores, each checked first, so a refusal does not tell which stores exist; else every store the user reads.
    private async Task<IList<string>> GetStoreIdsAsync(ClaimsPrincipal user, string[] storeIds)
    {
        var named = (storeIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var storeId in named)
        {
            await authorizationService.EnsureAllowedAsync(user, new SiteBuilderPageSearchCriteria { StoreId = storeId }, storeId);
        }

        if (named.Count > 0)
        {
            return named;
        }

        var all = await authorizationService.GetStoreIdsAsync(user, userManagerFactory);
        if (all?.Count == 0)
        {
            throw new McpForbiddenException("Your builder:read permission does not name a store you work in, so there are no pages to change.");
        }

        return all;
    }

    private async Task<IList<string>> GetSharedComponentStoreIdsAsync(ClaimsPrincipal user, IList<string> storeIds)
    {
        if (storeIds == null)
        {
            return await authorizationService.GetStoreIdsAsync(user, userManagerFactory, SharedComponentsRead);
        }

        var result = new List<string>();
        foreach (var storeId in storeIds)
        {
            if (await authorizationService.IsAllowedAsync(user, new SiteBuilderPageSearchCriteria { StoreId = storeId }, SharedComponentsRead))
            {
                result.Add(storeId);
            }
        }

        return result;
    }

    private async Task<SectionBulkPreview> PreviewAsync(ClaimsPrincipal user, SiteBuilderSectionScanResult scan, IList<UpdateSectionsBulkJobPage> targets,
        string sectionType, string op, Dictionary<string, JsonNode> settings, string language, bool dryRun, CancellationToken cancellationToken)
    {
        var componentSections = scan.Pages.SelectMany(x => x.Sections).Count(x => x.SharedComponentId != null);
        var preview = new SectionBulkPreview
        {
            Action = "update_sections_bulk",
            ObjectType = "pages",
            ObjectName = $"{op} on {sectionType} sections",
            DryRun = dryRun,
            TotalPageCount = scan.TotalPageCount,
            ScannedPageCount = scan.ScannedPageCount,
            IsComplete = scan.IsComplete,
            Coverage = SectionScanCoverage.Describe(scan),
            TargetPageCount = targets.Count,
            TargetSectionCount = targets.Sum(x => x.SectionIds.Count),
            SharedComponentSectionCount = componentSections,
        };

        var pages = scan.Pages.ToDictionary(x => x.Page.Id, StringComparer.Ordinal);
        foreach (var target in targets.Take(MaxPreviewPages))
        {
            var page = pages[target.PageId].Page;
            var result = await runner.RunAsync(user, target.PageId, target.VersionToken,
                SectionBulkChanges.CreateOperations(op, settings, language, target.SectionIds), dryRun: true, cancellationToken);
            var label = $"page '{page.Name}' ({page.Id}, {page.CultureName})";
            foreach (var change in result.Changes ?? [])
            {
                preview.AddChange($"{label}: {change.Field}", change.From, change.To);
            }

            if (result.Outcome is not (PageJobOutcome.Changed or PageJobOutcome.Unchanged))
            {
                preview.AddWarning($"{label} would not change: {result.Reason}.");
            }

            preview.Pages.Add(new SectionBulkPageRow
            {
                PageId = page.Id,
                Name = page.Name,
                Language = page.CultureName,
                StoreId = page.StoreId,
                SectionIds = target.SectionIds,
                Problem = result.Outcome is PageJobOutcome.Changed or PageJobOutcome.Unchanged ? null : result.Reason,
            });
        }

        if (targets.Count > MaxPreviewPages)
        {
            preview.AddWarning($"The change is shown on the first {MaxPreviewPages} of {targets.Count} pages.");
        }

        if (!scan.IsComplete)
        {
            preview.AddWarning($"{preview.Coverage} The real call scans every page of the filters, up to {MaxPages}.");
        }

        if (componentSections > 0)
        {
            preview.AddWarning($"{componentSections} matching sections come from placed Shared Components and are not changed: " +
                "a change of a component shows on every page it is placed on, so it is made to the component itself.");
        }

        if (targets.Count == 0)
        {
            preview.AddWarning(scan.IsComplete
                ? "No page has a section of its own that matches, so there is nothing to change."
                : "No page among those scanned has a section of its own that matches.");
        }

        return preview;
    }
}

public sealed class SectionBulkPreview : McpChangePreview
{
    // Pages the filters select, and how many of them were scanned.
    [JsonPropertyName("total_page_count")]
    public int TotalPageCount { get; set; }

    [JsonPropertyName("scanned_page_count")]
    public int ScannedPageCount { get; set; }

    [JsonPropertyName("is_complete")]
    public bool IsComplete { get; set; }

    [JsonPropertyName("coverage")]
    public string Coverage { get; set; }

    // Language versions with a section of their own to change, and those sections.
    [JsonPropertyName("target_page_count")]
    public int TargetPageCount { get; set; }

    [JsonPropertyName("target_section_count")]
    public int TargetSectionCount { get; set; }

    [JsonPropertyName("shared_component_section_count")]
    public int SharedComponentSectionCount { get; set; }

    // The first pages, with the sections the change applies to.
    [JsonPropertyName("pages")]
    public IList<SectionBulkPageRow> Pages { get; set; } = [];
}

public sealed class SectionBulkPageRow
{
    [JsonPropertyName("page_id")]
    public string PageId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("language")]
    public string Language { get; init; }

    [JsonPropertyName("store_id")]
    public string StoreId { get; init; }

    [JsonPropertyName("section_ids")]
    public IList<string> SectionIds { get; init; } = [];

    // Why the change would not apply to this page.
    [JsonPropertyName("problem")]
    public string Problem { get; init; }
}
