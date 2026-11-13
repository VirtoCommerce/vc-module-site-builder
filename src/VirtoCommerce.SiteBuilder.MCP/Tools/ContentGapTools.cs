using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// What is missing on the pages of the stores the user reads: languages a Multilanguage Page lacks or whose translation
// is behind, and pages without an SEO title or description. The pages are read one by one, a page of the pages list per
// call, and the answer says how far it got. The checks that need data Site Builder does not hold are named as not checked,
// with the reason, rather than reported as clean.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class ContentGapTools(
    IAuthorizationService authorizationService,
    Func<UserManager<ApplicationUser>> userManagerFactory,
    ISiteBuilderPageListService pageListService,
    IGroupedPageContentService pageContentService)
{
    public const string TranslationsCategory = "translations";
    public const string SeoCategory = "seo";
    public const string ReferencesCategory = "references";
    public const string SchemaCategory = "schema";

    private const string SeoInfoKey = "seoInfo";
    private const string SeoTitleKey = "pageTitle";
    private const string SeoDescriptionKey = "metaDescription";

    private static readonly string[] Categories = [TranslationsCategory, SeoCategory, ReferencesCategory, SchemaCategory];

    private static readonly ContentGapCheck[] NotCheckable =
    [
        new(ReferencesCategory, "Not available: links to products and categories that were deleted cannot be checked, because Site Builder " +
            "does not depend on the Catalog module and does not read products or categories."),
        new(SchemaCategory, "Not available: no service checks section settings against the storefront's schema catalog; saving a page " +
            "checks only links and HTML against it. A section the storefront does not know shows nothing on the page."),
    ];

    [McpServerTool(Name = "find_content_gaps", ReadOnly = true, Destructive = false, Idempotent = true)]
    [McpPermission(Read)]
    [Description("Find what is missing on the pages of a store, as findings with evidence and a fix: a language a Multilanguage " +
        "Page has no version in (translation-missing), a translation behind its source language (translation-outdated), and " +
        "a page without an SEO title or description (seo-title-missing, seo-description-missing), read in the draft, which " +
        "is what the next publication shows. Archived pages are left out. The pages are read take at a time; continue with " +
        "next_skip until is_complete before saying the site has no gaps. Links to deleted products and categories and section " +
        "settings against the schemas are not checked; not_checked says why. Fix with create_page (kind language_version), " +
        "set_translations and edit_page_draft.")]
    public async Task<ContentGapReport> FindContentGaps(
        RequestContext<CallToolRequestParams> context,
        IMcpPageSizeResolver pageSizeResolver,
        [Description("Store id. Omit it to check every store you read pages of.")] string store_id = null,
        [Description("Only this kind of gap: translations or seo. references and schema are answered with the reason they are not checked.")] string category = null,
        [Description("Language code, for example fr-FR: only gaps in this language (pages in it, and Multilanguage Pages missing it or behind in it).")] string language = null,
        [Description("Pages of the pages list to skip, from 0. Use next_skip of the previous answer.")] int? skip = null,
        [Description("Pages of the pages list to read in this call. Omit it for the default page size.")] int? take = null,
        CancellationToken cancellationToken = default)
    {
        var kind = ParseCategory(category);
        var criteria = new SiteBuilderPageSearchCriteria
        {
            StoreId = Trim(store_id),
            Statuses = $"{PageStatuses.Draft},{PageStatuses.Published}",
            LanguageCode = Trim(language),
            Sort = $"{nameof(SiteBuilderPageListItem.ModifiedDate)}:desc;{nameof(SiteBuilderPageListItem.CreatedDate)}:desc",
        }.WithPaging(await pageSizeResolver.GetPagingAsync(skip, take));

        await NarrowToStoresAsync(context, criteria);

        var report = new ContentGapReport { NotChecked = NotCheckable.Where(x => kind == null || x.Category == kind).ToList() };
        if (kind is ReferencesCategory or SchemaCategory)
        {
            report.Coverage = "Nothing was read: this category is not checked.";
            return report;
        }

        var rows = await pageListService.SearchAsync(criteria, cancellationToken);
        var findings = new List<McpFinding>();
        foreach (var row in rows.Results)
        {
            await CheckRowAsync(row, kind, criteria.LanguageCode, findings, cancellationToken);
        }

        report.Complete(findings, criteria.Skip, rows.Results.Count, rows.TotalCount);

        return report;
    }

    private static string Trim(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task NarrowToStoresAsync(RequestContext<CallToolRequestParams> context, SiteBuilderPageSearchCriteria criteria)
    {
        if (!string.IsNullOrEmpty(criteria.StoreId))
        {
            await authorizationService.EnsureAllowedAsync(context.User, new SiteBuilderPageSearchCriteria { StoreId = criteria.StoreId }, criteria.StoreId);
            return;
        }

        criteria.StoreIds = await authorizationService.GetStoreIdsAsync(context.User, userManagerFactory);
        if (criteria.StoreIds?.Count == 0)
        {
            throw new McpForbiddenException("Your builder:read permission does not name a store you work in, so there are no pages to check.");
        }
    }

    private static string ParseCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        return Categories.FirstOrDefault(x => x.EqualsIgnoreCase(category.Trim()))
            ?? throw new McpException($"category must be one of {string.Join(", ", Categories)}.");
    }

    private async Task CheckRowAsync(SiteBuilderPageListItem row, string kind, string language, IList<McpFinding> findings, CancellationToken cancellationToken)
    {
        if (row.MultilanguagePage != null && kind is null or TranslationsCategory)
        {
            AddTranslationGaps(row, language, findings);
        }

        if (kind is not (null or SeoCategory))
        {
            return;
        }

        foreach (var page in PagesOf(row, language))
        {
            await AddSeoGapsAsync(page, findings, cancellationToken);
        }
    }

    // A Landing Page, or the language versions a Multilanguage Page has (in the language, when one is named).
    private static IEnumerable<GroupedSiteBuilderPage> PagesOf(SiteBuilderPageListItem row, string language)
    {
        if (row.Page != null)
        {
            return [row.Page];
        }

        return (row.Languages ?? [])
            .Where(x => x.Page != null && x.Page.Status != PageStatuses.Archived)
            .Where(x => language == null || x.CultureName.EqualsIgnoreCase(language))
            .Select(x => x.Page);
    }

    private static void AddTranslationGaps(SiteBuilderPageListItem row, string language, IList<McpFinding> findings)
    {
        var root = row.MultilanguagePage;
        var source = row.Languages?.FirstOrDefault(x => x.IsDefault)?.CultureName ?? root.DefaultCultureName;
        foreach (var item in (row.Languages ?? []).Where(x => language == null || x.CultureName.EqualsIgnoreCase(language)))
        {
            if (item.Status == MultilanguagePageLanguage.Missing && item.IsStoreLanguage)
            {
                findings.Add(Gap("translation-missing", McpFindingSeverities.Warning, $"'{root.Name}' has no {item.CultureName} version.",
                    $"Multilanguage Page '{root.Id}' in store '{root.StoreId}': {item.CultureName} is Missing; the source language is {source}.",
                    $"Create it with create_page (kind language_version, multilanguage_page_id '{root.Id}', language '{item.CultureName}'), then translate it with set_translations.",
                    TranslationsCategory, ChangePreview.MultilanguagePage, root.Id, root.Name));
            }
            else if (item.IsOutdated)
            {
                findings.Add(Gap("translation-outdated", McpFindingSeverities.Warning, $"The {item.CultureName} version of '{root.Name}' is behind its source language.",
                    $"Page '{item.Page?.Id}': {source} was published after this translation.",
                    "Read both versions with search_site_pages, translate what changed with set_translations, and mark it up to date there.",
                    TranslationsCategory, ChangePreview.Page, item.Page?.Id, root.Name));
            }
        }
    }

    private async Task AddSeoGapsAsync(GroupedSiteBuilderPage page, IList<McpFinding> findings, CancellationToken cancellationToken)
    {
        string content;
        try
        {
            content = (await pageContentService.LoadGroupContentAsync(page, draft: true, cancellationToken)).Content;
        }
        catch (KeyNotFoundException)
        {
            return;
        }

        var seo = SiteBuilderPageDocument.Read(content)?.Settings?[SeoInfoKey] as JsonObject;
        var severity = page.Status == PageStatuses.Published ? McpFindingSeverities.Warning : McpFindingSeverities.Info;
        var name = $"{page.Name} ({page.CultureName})";
        if (!HasText(seo?[SeoTitleKey], page.CultureName))
        {
            findings.Add(Gap("seo-title-missing", severity, $"'{name}' has no SEO title: search engines show the page name instead.",
                $"Page '{page.Id}' in store '{page.StoreId}': settings.seoInfo.pageTitle is empty in the draft.",
                $"Set it with edit_page_draft (set_page_settings, path seoInfo.pageTitle) on page '{page.Id}'.", SeoCategory, ChangePreview.Page, page.Id, name));
        }

        if (!HasText(seo?[SeoDescriptionKey], page.CultureName))
        {
            findings.Add(Gap("seo-description-missing", severity, $"'{name}' has no SEO description: search engines pick a text of the page.",
                $"Page '{page.Id}' in store '{page.StoreId}': settings.seoInfo.metaDescription is empty in the draft.",
                $"Set it with edit_page_draft (set_page_settings, path seoInfo.metaDescription) on page '{page.Id}'.", SeoCategory, ChangePreview.Page, page.Id, name));
        }
    }

    // A text, or a text per language: the page's language counts, else any language.
    private static bool HasText(JsonNode node, string language)
    {
        if (node is JsonValue value)
        {
            return value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text);
        }

        if (node is not JsonObject texts)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(language) && texts[language] is { } own)
        {
            return HasText(own, language: null);
        }

        return texts.Any(x => HasText(x.Value, language: null));
    }

    private static McpFinding Gap(string id, string severity, string title, string evidence, string fix, string category, string objectType, string objectId,
        string objectName) => new()
    {
        Id = id,
        Severity = severity,
        Title = title,
        Evidence = evidence,
        Fix = fix,
        Category = category,
        ObjectType = objectType,
        ObjectId = objectId,
        ObjectName = objectName,
    };
}

public sealed class ContentGapReport
{
    public IList<McpFinding> Findings { get; set; } = [];

    // Pages of the pages list the filters match: a Landing Page, or a Multilanguage Page with all its languages.
    public int TotalCount { get; set; }

    // Pages read in this call.
    public int ScannedCount { get; set; }

    public int Skip { get; set; }

    // Pass it as skip to read the next pages; null when every page was read.
    public int? NextSkip { get; set; }

    public bool IsComplete => NextSkip == null;

    public string Coverage { get; set; }

    // The findings of the pages read from skip on, and how far the reading got.
    public void Complete(IEnumerable<McpFinding> findings, int skip, int scanned, int totalCount)
    {
        Findings = findings.OrderBySeverity();
        TotalCount = totalCount;
        ScannedCount = scanned;
        Skip = skip;
        NextSkip = scanned > 0 && skip + scanned < totalCount ? skip + scanned : null;
        var next = IsComplete ? ": every page was read." : $"; continue with skip {NextSkip}.";
        Coverage = $"Read pages {skip + 1}–{skip + scanned} of {totalCount}{next}";
    }

    // Kinds of gaps this tool does not check, with the reason.
    public IList<ContentGapCheck> NotChecked { get; init; } = [];
}

public sealed record ContentGapCheck(string Category, string Reason);
