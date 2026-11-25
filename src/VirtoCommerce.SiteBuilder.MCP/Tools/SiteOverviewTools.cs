using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
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
using VirtoCommerce.SiteBuilder.MCP.Extensions;
using VirtoCommerce.SiteBuilder.MCP.Services;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Model.Search;
using VirtoCommerce.StoreModule.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The services come from dependency injection; a parameter object would only hide them and change the constructor that subclasses call.")]
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
    ISiteBuilderPublishStatusQueryService publishStatusQueryService,
    ILogger<SiteOverviewTools> logger)
{
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
            await authorizationService.EnsureStoreAllowedAsync(context.User, storeId);
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
            return new FeatureState { Available = false, State = "unknown", Note = $"Not available. {GitContentFailures.Describe(logger, ex)}" };
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
                ? "Git flow: a page stored as a file goes live through a pull request in the store's repository, and list_versions reads its commits."
                : "Pages stored as files go live at once in blob storage and keep no versions. Database pages go live at once in either flow.",
        };
    }
}

public sealed class StoreOverview
{
    public string StoreId { get; init; }

    public string StoreName { get; init; }

    public string DefaultLanguage { get; init; }

    public IList<string> Languages { get; init; } = [];

    public string TimeZone { get; init; }

    public PageCounts Pages { get; init; }

    public IList<PageChangeRow> WaitingForPublication { get; set; }

    public IList<PageChangeRow> RecentChanges { get; set; }

    public IList<LanguageCoverage> LanguageCoverage { get; set; }

    public ThemeOverview Theme { get; init; }

    public FeatureState History { get; init; }

    public FeatureState SchemaCatalog { get; init; }

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

    public int WithDrafts { get; init; }

    public int LiveNow { get; init; }

    public int Scheduled { get; init; }

    public int EndedOrArchived { get; init; }

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

    public int Missing { get; init; }

    public int Outdated { get; init; }
}

public sealed class PageChangeRow
{
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

    public int? Publication { get; init; }

    public int DraftFiles { get; init; }

    public int Templates { get; init; }

    public IList<string> TemplatesWithDrafts { get; init; } = [];

    public IList<string> UnpublishedTemplates { get; init; } = [];

    public RulesOverview Rules { get; set; }

    public IList<ThemePartState> Parts { get; set; }

    public string Note { get; init; }

    public IList<string> Notes { get; init; } = [];
}

public sealed class RulesOverview
{
    public int Rules { get; init; }

    public int Disabled { get; init; }

    public IList<string> OwnRulesFor { get; init; } = [];

    public IList<string> StorefrontDefaultsFor { get; init; } = [];

    public bool HasDraft { get; init; }
}

public sealed class ThemePartState
{
    public string Part { get; init; }

    public string File { get; init; }

    public string State { get; init; }
}

public sealed class FeatureState
{
    public bool Available { get; init; }

    public string State { get; init; }

    public string Note { get; init; }
}
