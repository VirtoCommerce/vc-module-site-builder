using System.ComponentModel;
using System.Security.Claims;
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
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Model.Search;
using VirtoCommerce.StoreModule.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// "Start here": what a store's site holds and where it stands, read from the services the designer's lists, the theme
// panel and the history use. Every number says what it counts, and a part that cannot be read says why instead of
// showing zero.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class SiteOverviewTools(
    IAuthorizationService authorizationService,
    Func<UserManager<ApplicationUser>> userManagerFactory,
    IStoreService storeService,
    IStoreSearchService storeSearchService,
    ISiteBuilderPageListService pageListService,
    SiteThemeStates themeStates,
    IContentHistory contentHistory,
    ISchemaCatalogService schemaCatalogService,
    ISiteBuilderPublishStatusQueryService publishStatusQueryService)
{
    // A store's overview reads a few dozen counts and files; more stores than this in one call are paged.
    private const int MaxStores = 10;

    private const int ListedRows = 5;

    [McpServerTool(Name = "get_site_overview", ReadOnly = true, Destructive = false, Idempotent = true)]
    [McpPermission(Read)]
    [Description("Start here. What a store's site holds and where it stands: pages by status and by language, the pages " +
        "waiting for publication and the latest changes with who made them and when, how complete the translations of " +
        "Multilanguage Pages are (Missing and Outdated per language), the theme (templates and those with drafts, template " +
        "rules, header, footer, brand and palette: draft, published or the storefront's default), whether the store keeps " +
        "a version history, whether the storefront has sent its schema catalog, and whether pages stored as files go live " +
        "through git. Without store_id it answers every store you read, a page of stores at a time, in a short form; pass " +
        "store_id for the full form. Next: search_site_pages and search_templates for details, find_content_gaps for what " +
        "is missing, get_publish_readiness before a publication.")]
    public async Task<McpPage<StoreOverview>> GetSiteOverview(
        RequestContext<CallToolRequestParams> context,
        IMcpPageSizeResolver pageSizeResolver,
        [Description("Store id. Omit it for every store you read pages of, in the short form: counts, theme drafts, history, " +
            "catalog and publishing, without the lists of pages and the language coverage.")] string store_id = null,
        [Description("Without store_id: stores to skip, from 0. Use next_skip of the previous answer.")] int? skip = null,
        [Description("Without store_id: stores to return, at most 10.")] int? take = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(store_id))
        {
            var storeId = store_id.Trim();
            await authorizationService.EnsureAllowedAsync(context.User, new SiteBuilderPageSearchCriteria { StoreId = storeId }, storeId);
            var store = await storeService.GetNoCloneAsync(storeId)
                ?? throw new McpException($"Store '{storeId}' was not found. Call get_site_overview without store_id for your stores.");

            return McpPage<StoreOverview>.Create([await ReadStoreAsync(context.User, store, full: true, cancellationToken)], 1, new McpPaging(0, 1));
        }

        var paging = await pageSizeResolver.GetPagingAsync(skip, take);
        paging = paging with { Take = Math.Min(paging.Take, MaxStores) };
        var (stores, totalCount) = await GetStoresAsync(context.User, paging);

        var rows = new List<StoreOverview>();
        foreach (var store in stores)
        {
            rows.Add(await ReadStoreAsync(context.User, store, full: false, cancellationToken));
        }

        return McpPage<StoreOverview>.Create(rows, totalCount, paging);
    }

    // The stores the user reads pages of, as search_site_pages resolves them: every store for a permission granted in
    // every store, else the stores of the scope or of the account.
    private async Task<(IList<Store> Stores, int TotalCount)> GetStoresAsync(ClaimsPrincipal user, McpPaging paging)
    {
        var storeIds = await authorizationService.GetStoreIdsAsync(user, userManagerFactory);
        if (storeIds == null)
        {
            var result = await storeSearchService.SearchAsync(new StoreSearchCriteria { Sort = nameof(Store.Name), Skip = paging.Skip, Take = paging.Take }, clone: false);

            return (result.Results, result.TotalCount);
        }

        if (storeIds.Count == 0)
        {
            throw new McpForbiddenException("Your builder:read permission does not name a store you work in, so there is no site to describe.");
        }

        var stores = new List<Store>();
        foreach (var storeId in storeIds.Order(StringComparer.OrdinalIgnoreCase).Skip(paging.Skip).Take(paging.Take))
        {
            stores.Add(await storeService.GetNoCloneAsync(storeId) ?? new Store { Id = storeId, Name = storeId });
        }

        return (stores, storeIds.Count);
    }

    private async Task<StoreOverview> ReadStoreAsync(ClaimsPrincipal user, Store store, bool full, CancellationToken cancellationToken)
    {
        var overview = new StoreOverview
        {
            StoreId = store.Id,
            StoreName = store.Name,
            DefaultLanguage = store.DefaultLanguage,
            Languages = store.Languages?.ToList() ?? [],
            TimeZone = store.TimeZone,
            Pages = await CountPagesAsync(store, full, cancellationToken),
            Theme = await themeStates.ReadAsync(user, store.Id, full, cancellationToken),
            History = await ReadHistoryAsync(store.Id, cancellationToken),
            SchemaCatalog = await ReadCatalogAsync(store.Id, cancellationToken),
            Publishing = await ReadPublishingAsync(user, store.Id, cancellationToken),
        };

        if (full)
        {
            overview.WaitingForPublication = await ListPagesAsync(store.Id, PageLifecycleFilters.Drafts, cancellationToken);
            overview.RecentChanges = await ListPagesAsync(store.Id, lifecycle: null, cancellationToken);
            overview.LanguageCoverage = await ReadCoverageAsync(store, cancellationToken);
        }
        else
        {
            overview.Notes.Add("Short form: call get_site_overview with this store_id for the pages waiting for publication, the latest changes, " +
                "the pages per language, the language coverage and the theme's parts.");
        }

        return overview;
    }

    private async Task<PageCounts> CountPagesAsync(Store store, bool full, CancellationToken cancellationToken)
    {
        var counts = new PageCounts
        {
            Total = await CountAsync(store.Id, _ => { }, cancellationToken),
            LandingPages = await CountAsync(store.Id, x => x.PageType = PageTypes.Landing, cancellationToken),
            MultilanguagePages = await CountAsync(store.Id, x => x.PageType = PageTypes.Multilanguage, cancellationToken),
            WithDrafts = await CountAsync(store.Id, x => x.Lifecycle = PageLifecycleFilters.Drafts, cancellationToken),
            LiveNow = await CountAsync(store.Id, x => x.Lifecycle = PageLifecycleFilters.Active, cancellationToken),
            Scheduled = await CountAsync(store.Id, x => x.Lifecycle = PageLifecycleFilters.Pending, cancellationToken),
            EndedOrArchived = await CountAsync(store.Id, x => x.Lifecycle = PageLifecycleFilters.Archived, cancellationToken),
        };

        if (full)
        {
            foreach (var language in store.Languages ?? [])
            {
                counts.ByLanguage.Add(new LanguageCount { Language = language, Pages = await CountAsync(store.Id, x => x.LanguageCode = language, cancellationToken) });
            }
        }

        return counts;
    }

    // Multilanguage Pages whose language has no version, or a translation behind its source: the filters of the
    // designer's pages list.
    private async Task<IList<LanguageCoverage>> ReadCoverageAsync(Store store, CancellationToken cancellationToken)
    {
        var coverage = new List<LanguageCoverage>();
        foreach (var language in store.Languages ?? [])
        {
            coverage.Add(new LanguageCoverage
            {
                Language = language,
                Missing = await CountAsync(store.Id, x => SetTranslationFilter(x, language, TranslationStatuses.Missing), cancellationToken),
                Outdated = await CountAsync(store.Id, x => SetTranslationFilter(x, language, TranslationStatuses.Outdated), cancellationToken),
            });
        }

        return coverage;
    }

    private static void SetTranslationFilter(SiteBuilderPageSearchCriteria criteria, string language, string status)
    {
        criteria.PageType = PageTypes.Multilanguage;
        criteria.LanguageCode = language;
        criteria.TranslationStatus = status;
    }

    private async Task<int> CountAsync(string storeId, Action<SiteBuilderPageSearchCriteria> filter, CancellationToken cancellationToken)
    {
        var criteria = new SiteBuilderPageSearchCriteria { StoreId = storeId, Take = 0 };
        filter(criteria);

        return (await pageListService.SearchAsync(criteria, cancellationToken)).TotalCount;
    }

    private async Task<IList<PageChangeRow>> ListPagesAsync(string storeId, string lifecycle, CancellationToken cancellationToken)
    {
        var result = await pageListService.SearchAsync(new SiteBuilderPageSearchCriteria
        {
            StoreId = storeId,
            Lifecycle = lifecycle,
            Sort = $"{nameof(SiteBuilderPageListItem.ModifiedDate)}:desc;{nameof(SiteBuilderPageListItem.CreatedDate)}:desc",
            Take = ListedRows,
        }, cancellationToken);

        return result.Results.Select(PageChangeRow.From).ToList();
    }

    private async Task<FeatureState> ReadHistoryAsync(string storeId, CancellationToken cancellationToken)
    {
        var status = await contentHistory.GetStatusAsync(storeId, cancellationToken);
        var (state, note) = ContentVersionsAnswer.Describe(storeId, status);

        return new FeatureState
        {
            Available = status == ContentHistoryStatus.Enabled,
            State = state,
            Note = note ?? "Every save of a database page and of a theme file is a version: list_versions and restore_version use them.",
        };
    }

    private async Task<FeatureState> ReadCatalogAsync(string storeId, CancellationToken cancellationToken)
    {
        var catalog = await ThemeFiles.GetCatalogAsync(schemaCatalogService, storeId, cancellationToken);
        if (catalog == null)
        {
            return new FeatureState
            {
                Available = false,
                State = "not_sent",
                Note = "Not available: the storefront has not sent its schema catalog yet, so theme files cannot be saved or published " +
                    "here and the section schemas cannot be read. Open the store once in the designer; its preview sends the catalog.",
            };
        }

        var sections = (catalog[SchemaKinds.Sections] as JsonObject)?.Count ?? 0;
        var blocks = (catalog[SchemaKinds.Blocks] as JsonObject)?.Count ?? 0;

        return new FeatureState
        {
            Available = true,
            State = "sent",
            Note = $"{sections} section types and {blocks} block types; the resource site-builder://stores/{storeId}/schemas lists them.",
        };
    }

    private async Task<FeatureState> ReadPublishingAsync(ClaimsPrincipal user, string storeId, CancellationToken cancellationToken)
    {
        SiteBuilderOperationResult<PagePublishStatus> result;
        try
        {
            result = await publishStatusQueryService.GetFilePageStatusAsync(new SiteBuilderFilePageQueryRequest { StoreId = storeId }, user, cancellationToken);
        }
        catch (GitContentException ex)
        {
            return new FeatureState { Available = false, State = "unknown", Note = $"Not available: the store's content repository did not answer ({ex.Message})." };
        }

        if (result?.Succeeded != true || result.Value == null)
        {
            return new FeatureState { Available = false, State = "unknown", Note = $"Not available: the publication flow could not be read ({result?.Error ?? "no answer"})." };
        }

        var git = result.Value.Flow == "git";

        return new FeatureState
        {
            Available = true,
            State = result.Value.Flow,
            Note = git
                ? "Git flow: a page stored as a file goes live through a pull request in the store's repository."
                : "Pages stored as files go live at once in blob storage. Database pages go live at once in either flow.",
        };
    }
}

// The theme's state as the designer's theme panel shows it: the publication, the templates and their drafts, the rules,
// and the header, footer, brand and palette files.
public class SiteThemeStates(
    ISiteBuilderContentQueryService contentQueryService,
    IDesignerOperationService designerOperationService,
    IThemeOperationService themeOperationService,
    IThemeStorageFactory themeStorageFactory)
{
    private static readonly (string Part, string File)[] Parts =
    [
        (ThemeDraftTools.HeaderPart, ThemeStorage.HeaderGroupFile),
        (ThemeDraftTools.FooterPart, ThemeStorage.FooterGroupFile),
        (ThemeDraftTools.BrandPart, ThemeStorage.BrandFile),
        (ThemeDraftTools.PalettePart, ThemeStorage.PaletteFile),
        ("palette_dark", ThemeStorage.DarkPaletteFile),
    ];

    public async Task<ThemeOverview> ReadAsync(ClaimsPrincipal user, string storeId, bool full, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadThemeAsync(user, storeId, full, cancellationToken);
        }
        catch (McpException ex) when (ex is not McpForbiddenException)
        {
            return new ThemeOverview { Available = false, Note = $"Not available: {ex.Message}" };
        }
    }

    private async Task<ThemeOverview> ReadThemeAsync(ClaimsPrincipal user, string storeId, bool full, CancellationToken cancellationToken)
    {
        var status = await themeOperationService.GetStatusAsync(new SiteBuilderOperationRequest { StoreId = storeId }, user, cancellationToken);
        if (!status.Succeeded || status.Value == null)
        {
            return new ThemeOverview { Available = false, Note = $"Not available: the theme's state could not be read ({status.Error ?? status.Status.ToString()})." };
        }

        var templates = await ThemeTemplates.ListAsync(contentQueryService, user, storeId, cancellationToken);
        var overview = new ThemeOverview
        {
            Available = true,
            Publication = status.Value.Publication,
            DraftFiles = status.Value.Drafts.Count,
            Templates = templates.Count,
            TemplatesWithDrafts = templates.Where(x => x.HasDraft).Select(x => x.Key).ToList(),
            UnpublishedTemplates = templates.Where(x => !x.Published).Select(x => x.Key).ToList(),
        };

        if (status.Value.Publication == null)
        {
            overview.Notes.Add("The theme was never published: the storefront shows its own defaults until publish_theme runs.");
        }

        if (full)
        {
            overview.Rules = await ReadRulesAsync(user, storeId, status.Value, cancellationToken);
            overview.Parts = await ReadPartsAsync(user, storeId, status.Value, cancellationToken);
        }

        if (overview.DraftFiles > 0)
        {
            overview.Notes.Add("get_publish_readiness with theme true lists every draft with who changed it.");
        }

        return overview;
    }

    private async Task<RulesOverview> ReadRulesAsync(ClaimsPrincipal user, string storeId, ThemeStatus status, CancellationToken cancellationToken)
    {
        var (rules, _) = await ThemeTemplates.ReadRulesAsync(designerOperationService, themeStorageFactory, user, storeId, cancellationToken);
        var all = TemplateRulesDocument.PageTypes.SelectMany(rules.RulesOf).ToList();

        return new RulesOverview
        {
            Rules = all.Count,
            Disabled = all.Count(TemplateRulesDocument.IsDisabled),
            OwnRulesFor = TemplateRulesDocument.PageTypes.Where(rules.HasOwnRules).ToList(),
            StorefrontDefaultsFor = TemplateRulesDocument.PageTypes.Where(x => !rules.HasOwnRules(x)).ToList(),
            HasDraft = IsDraft(status, ThemeStorage.TemplateRulesFile),
        };
    }

    private async Task<IList<ThemePartState>> ReadPartsAsync(ClaimsPrincipal user, string storeId, ThemeStatus status, CancellationToken cancellationToken)
    {
        var parts = new List<ThemePartState>();
        foreach (var (part, file) in Parts)
        {
            parts.Add(new ThemePartState { Part = part, File = file, State = await GetPartStateAsync(user, storeId, status, file, cancellationToken) });
        }

        return parts;
    }

    private async Task<string> GetPartStateAsync(ClaimsPrincipal user, string storeId, ThemeStatus status, string file, CancellationToken cancellationToken)
    {
        if (IsDraft(status, file))
        {
            return status.NewDrafts.Contains(file, StringComparer.OrdinalIgnoreCase) ? "draft_never_published" : "draft";
        }

        var path = await ThemeFiles.GetPathAsync(themeStorageFactory, storeId, file);

        return await ThemeFiles.ReadLiveAsync(contentQueryService, user, storeId, path, cancellationToken) == null ? "storefront_default" : "published";
    }

    private static bool IsDraft(ThemeStatus status, string file) => status.Drafts.Contains(file, StringComparer.OrdinalIgnoreCase);
}

public sealed class StoreOverview
{
    public string StoreId { get; init; }

    public string StoreName { get; init; }

    public string DefaultLanguage { get; init; }

    public IList<string> Languages { get; init; } = [];

    // The store's time zone: start and end dates of pages are stored in UTC.
    public string TimeZone { get; init; }

    public PageCounts Pages { get; init; }

    // Full form: the pages with a draft, most recently changed first; get_publish_readiness tells what each would publish.
    public IList<PageChangeRow> WaitingForPublication { get; set; }

    // Full form: the pages changed last, with who changed them.
    public IList<PageChangeRow> RecentChanges { get; set; }

    // Full form: per store language, the Multilanguage Pages that miss it or whose translation is outdated.
    public IList<LanguageCoverage> LanguageCoverage { get; set; }

    public ThemeOverview Theme { get; init; }

    // A version history of database pages and theme files, kept in the store's GitHub repository.
    public FeatureState History { get; init; }

    // The section schemas the storefront sent: theme files are checked against them.
    public FeatureState SchemaCatalog { get; init; }

    // git or blob: how pages stored as files go live.
    public FeatureState Publishing { get; init; }

    public IList<string> Notes { get; init; } =
    [
        "Page counts count rows of the pages list: a Landing Page, or a Multilanguage Page with all its languages, is one row. " +
        "A row with a published version and a draft counts both in with_drafts and in live_now.",
    ];
}

public sealed class PageCounts
{
    public int Total { get; init; }

    public int LandingPages { get; init; }

    public int MultilanguagePages { get; init; }

    // Pages with a draft: never published, or changed since their publication. They wait for publish_page.
    public int WithDrafts { get; init; }

    // Published and inside their start and end dates: what customers see now.
    public int LiveNow { get; init; }

    // Published with a start date still ahead.
    public int Scheduled { get; init; }

    // Archived, or past their end date.
    public int EndedOrArchived { get; init; }

    // Full form: rows with a version in the language.
    public IList<LanguageCount> ByLanguage { get; init; } = [];
}

public sealed class LanguageCount
{
    public string Language { get; init; }

    public int Pages { get; init; }
}

public sealed class LanguageCoverage
{
    public string Language { get; init; }

    // Multilanguage Pages without a version in the language.
    public int Missing { get; init; }

    // Multilanguage Pages whose version in the language is behind its source language's publication.
    public int Outdated { get; init; }
}

public sealed class PageChangeRow
{
    // A Landing Page's id, or a Multilanguage Page's id: search_site_pages reads either.
    public string Id { get; init; }

    public string Type { get; init; }

    public string Name { get; init; }

    public string Permalink { get; init; }

    public string ChangedBy { get; init; }

    public DateTime? ChangedDate { get; init; }

    public static PageChangeRow From(SiteBuilderPageListItem item)
    {
        var row = SitePageRow.From(item);

        return new PageChangeRow
        {
            Id = row.Id,
            Type = row.Type,
            Name = row.Name,
            Permalink = row.Permalink,
            ChangedBy = row.ModifiedBy,
            ChangedDate = row.ModifiedDate,
        };
    }
}

public sealed class ThemeOverview
{
    public bool Available { get; init; }

    // The number of the live publication; null when the theme was never published.
    public int? Publication { get; init; }

    // Theme files with unpublished changes, every part counted.
    public int DraftFiles { get; init; }

    public int Templates { get; init; }

    public IList<string> TemplatesWithDrafts { get; init; } = [];

    // Templates that exist only as a draft: customers get them with the next publication.
    public IList<string> UnpublishedTemplates { get; init; } = [];

    // Full form.
    public RulesOverview Rules { get; set; }

    // Full form: header, footer, brand, palette and dark palette.
    public IList<ThemePartState> Parts { get; set; }

    public string Note { get; init; }

    public IList<string> Notes { get; init; } = [];
}

public sealed class RulesOverview
{
    // Rules of every page type, as the designer edits them (the draft, else the live file).
    public int Rules { get; init; }

    public int Disabled { get; init; }

    // Page types with rules of the theme's own.
    public IList<string> OwnRulesFor { get; init; } = [];

    // Page types that use the storefront's default rules.
    public IList<string> StorefrontDefaultsFor { get; init; } = [];

    public bool HasDraft { get; init; }
}

public sealed class ThemePartState
{
    public string Part { get; init; }

    // The file in the theme folder.
    public string File { get; init; }

    // draft (unpublished changes), draft_never_published, published, or storefront_default (the theme has no such file).
    public string State { get; init; }
}

public sealed class FeatureState
{
    public bool Available { get; init; }

    public string State { get; init; }

    // What it means, or why it is not available.
    public string Note { get; init; }
}
