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
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// Database pages of the stores the user reads: the pages list of the designer, one row per Landing Page and per
// Multilanguage Page, and one page with its content.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class PageTools(
    IAuthorizationService authorizationService,
    ISiteBuilderPageListService pageListService,
    ISiteBuilderPageSearchService pageVersionSearchService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService pageContentService,
    Func<UserManager<ApplicationUser>> userManagerFactory)
{
    // The pages list has no date filter: the tool finds the pages by their versions changed in the period first. More
    // versions than this is a scan; the agent narrows the period instead.
    private const int MaxChangedVersions = 1000;

    private const string PublishedVersion = "published";
    private const string DraftVersion = "draft";

    [McpServerTool(Name = "search_site_pages", ReadOnly = true, Destructive = false, Idempotent = true)]
    [McpPermission(Read)]
    [Description("Find database pages of Site Builder (pages created in the designer, not pages from files). Returns one " +
        "page of compact rows: a Landing Page is a row with its language; a Multilanguage Page is one row with every " +
        "language version (page id, status, unpublished changes, outdated translation). Most recently changed first. " +
        "Pass page_id to read one page instead: its settings (name, address, language, audience, dates), its content " +
        "(page settings and sections, each with its id) and the version token of that content, which edit_page_draft takes.")]
    public async Task<object> SearchSitePages(
        RequestContext<CallToolRequestParams> context,
        IMcpPageSizeResolver pageSizeResolver,
        [Description("Store id. Omit it to search every store you read pages of.")] string store_id = null,
        [Description("Id of one page (a Landing Page, or a language version of a Multilanguage Page) to read with its " +
            "content. The other filters are ignored.")] string page_id = null,
        [Description("Text in the page name or address.")] string keyword = null,
        [Description("Draft (has unpublished changes or was never published), Published (has a published version) or " +
            "Archived. Several, comma-separated.")] string status = null,
        [Description("Language code, for example en-US: Landing Pages in it and Multilanguage Pages that have it.")] string language = null,
        [Description("Landing or Multilanguage. Omit it for both.")] string page_type = null,
        [Description("Pages changed at or after this UTC time, ISO 8601. A change of a Shared Component on the page counts.")] DateTime? modified_since = null,
        [Description("Pages not changed after this UTC time, ISO 8601.")] DateTime? modified_before = null,
        [Description("With page_id: 'draft' (default) for the content the designer edits, which is the published one " +
            "when the page has no unpublished changes, or 'published' for what the storefront shows.")] string version = DraftVersion,
        [Description("Rows to skip, from 0. Use next_skip of the previous page.")] int? skip = null,
        [Description("Rows to return. Omit it for the default page size.")] int? take = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(store_id))
        {
            // The store first, so an answer about the arguments does not tell which stores exist.
            await authorizationService.EnsureAllowedAsync(context.User, new SiteBuilderPageSearchCriteria { StoreId = store_id }, store_id);
        }

        if (!string.IsNullOrEmpty(page_id))
        {
            return await GetPageAsync(context, store_id, page_id, version, cancellationToken);
        }

        var criteria = new SiteBuilderPageSearchCriteria
        {
            StoreId = store_id,
            Keyword = keyword,
            Statuses = ParseStatuses(status),
            LanguageCode = language,
            PageType = ParsePageType(page_type),
            Sort = $"{nameof(SiteBuilderPageListItem.ModifiedDate)}:desc;{nameof(SiteBuilderPageListItem.CreatedDate)}:desc",
        }.WithPaging(await pageSizeResolver.GetPagingAsync(skip, take));

        if (string.IsNullOrEmpty(store_id))
        {
            criteria.StoreIds = await authorizationService.GetStoreIdsAsync(context.User, userManagerFactory);
            if (criteria.StoreIds?.Count == 0)
            {
                throw new McpForbiddenException("Your builder:read permission does not name a store you work in, so there are no pages to search.");
            }
        }

        var paging = new McpPaging(criteria.Skip, criteria.Take);
        if (modified_since != null || modified_before != null)
        {
            criteria.ObjectIds = await GetChangedPageIdsAsync(store_id, criteria.StoreIds, ToUtc(modified_since), ToUtc(modified_before));
            if (criteria.ObjectIds.Count == 0)
            {
                return McpPage<SitePageRow>.Create([], 0, paging);
            }
        }

        var result = await pageListService.SearchAsync(criteria, cancellationToken);

        return result.ToMcpPage(paging, SitePageRow.From);
    }

    private async Task<IList<string>> GetChangedPageIdsAsync(string storeId, IList<string> storeIds, DateTime? modifiedSince, DateTime? modifiedBefore)
    {
        var versions = await pageVersionSearchService.SearchAsync(new SiteBuilderPageSearchCriteria
        {
            StoreId = storeId,
            StoreIds = storeIds,
            ModifiedSince = modifiedSince,
            ModifiedBefore = modifiedBefore,
            Take = MaxChangedVersions,
        });

        if (versions.TotalCount > MaxChangedVersions)
        {
            throw new McpException($"More than {MaxChangedVersions} page versions changed in that period. Narrow modified_since and modified_before.");
        }

        return versions.Results
            .Select(x => x.GroupId)
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private async Task<SitePageDetails> GetPageAsync(RequestContext<CallToolRequestParams> context, string storeId, string pageId,
        string version, CancellationToken cancellationToken)
    {
        var published = (version ?? DraftVersion).ToLowerInvariant() switch
        {
            DraftVersion => false,
            PublishedVersion => true,
            _ => throw new McpException("version must be 'draft' or 'published'."),
        };

        var group = await groupedPageService.GetByIdAsync(pageId)
            ?? throw new McpException($"Page '{pageId}' was not found. Call search_site_pages without page_id to find its id.");

        await authorizationService.EnsureAllowedAsync(context.User, group, group.StoreId);

        if (!string.IsNullOrEmpty(storeId) && !storeId.EqualsIgnoreCase(group.StoreId))
        {
            throw new McpException($"Page '{pageId}' is not a page of store '{storeId}'.");
        }

        SiteBuilderContentSnapshot snapshot;
        try
        {
            snapshot = await pageContentService.LoadGroupContentAsync(group, draft: !published, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            throw new McpException($"Page '{pageId}' was deleted while it was read.");
        }

        return SitePageDetails.From(group, snapshot);
    }

    private static string ParseStatuses(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        string[] known = [PageStatuses.Draft, PageStatuses.Published, PageStatuses.Archived];
        var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => known.FirstOrDefault(y => y.EqualsIgnoreCase(x))
                ?? throw new McpException($"Status '{x}' is unknown. Use {string.Join(", ", known)}."))
            .Distinct();

        return string.Join(',', statuses);
    }

    private static string ParsePageType(string pageType)
    {
        if (string.IsNullOrWhiteSpace(pageType))
        {
            return null;
        }

        string[] known = [PageTypes.Landing, PageTypes.Multilanguage];

        return known.FirstOrDefault(x => x.EqualsIgnoreCase(pageType.Trim()))
            ?? throw new McpException($"Page type '{pageType}' is unknown. Use {string.Join(" or ", known)}.");
    }

    // A time without a kind is read as UTC, as the stored dates are.
    private static DateTime? ToUtc(DateTime? value) => value?.Kind switch
    {
        null => null,
        DateTimeKind.Local => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
    };
}

public sealed class SitePageRow
{
    // The page id of a Landing Page; the Multilanguage Page id of a Multilanguage Page.
    public string Id { get; init; }

    public string Type { get; init; }

    public string Name { get; init; }

    public string Permalink { get; init; }

    public string StoreId { get; init; }

    // A Landing Page only: its language, status and changes. A Multilanguage Page has them per language.
    public string Language { get; init; }

    public string Status { get; init; }

    public bool? HasUnpublishedChanges { get; init; }

    public IList<SitePageLanguageRow> Languages { get; init; }

    public DateTime? ModifiedDate { get; init; }

    public string ModifiedBy { get; init; }

    public static SitePageRow From(SiteBuilderPageListItem item)
    {
        var page = item.Page;
        var root = item.MultilanguagePage;

        return new SitePageRow
        {
            Id = item.Id,
            Type = item.Type,
            Name = item.Name,
            Permalink = item.Permalink,
            StoreId = page?.StoreId ?? root?.StoreId,
            Language = page?.CultureName,
            Status = page?.Status,
            HasUnpublishedChanges = page == null ? null : page.Status == PageStatuses.Published && page.HasChanges,
            Languages = item.Languages?.Select(SitePageLanguageRow.From).ToList(),
            ModifiedDate = item.ModifiedDate ?? item.CreatedDate,
            ModifiedBy = page?.ModifiedBy ?? page?.CreatedBy ?? root?.ModifiedBy ?? root?.CreatedBy,
        };
    }
}

public sealed class SitePageLanguageRow
{
    public string Language { get; init; }

    // The language version to pass as page_id; null when the language is Missing.
    public string PageId { get; init; }

    // Draft, Published, Archived, or Missing when the language has no version yet.
    public string Status { get; init; }

    public bool HasUnpublishedChanges { get; init; }

    // The source language was published after this translation.
    public bool IsOutdated { get; init; }

    public bool IsSourceLanguage { get; init; }

    public static SitePageLanguageRow From(MultilanguagePageLanguage language)
    {
        return new SitePageLanguageRow
        {
            Language = language.CultureName,
            PageId = language.Page?.Id,
            Status = language.Status,
            HasUnpublishedChanges = language.HasChanges,
            IsOutdated = language.IsOutdated,
            IsSourceLanguage = language.IsDefault,
        };
    }
}

public sealed class SitePageDetails
{
    public string Id { get; init; }

    public string StoreId { get; init; }

    public string Name { get; init; }

    public string Permalink { get; init; }

    public string Language { get; init; }

    public string Status { get; init; }

    public bool HasUnpublishedChanges { get; init; }

    public string MultilanguagePageId { get; init; }

    public bool Visibility { get; init; }

    public string UserGroups { get; init; }

    public string OrganizationId { get; init; }

    public DateTime? StartDate { get; init; }

    public DateTime? EndDate { get; init; }

    public DateTime CreatedDate { get; init; }

    public string CreatedBy { get; init; }

    public DateTime? ModifiedDate { get; init; }

    public string ModifiedBy { get; init; }

    // Draft or Published: the version the content below is of.
    public string ContentVersion { get; init; }

    // Changes with every save of the content.
    public string VersionToken { get; init; }

    public JsonObject Settings { get; init; }

    public JsonArray Sections { get; init; }

    // Why the content is missing, when it is.
    public string ContentNote { get; init; }

    public static SitePageDetails From(GroupedSiteBuilderPage group, SiteBuilderContentSnapshot snapshot)
    {
        var content = PageContent.Read(snapshot.Content);
        var shownPage = group.Pages?.FirstOrDefault(x => x.Id == snapshot.PageId);

        return new SitePageDetails
        {
            Id = group.Id,
            StoreId = group.StoreId,
            Name = group.Name,
            Permalink = group.Permalink,
            Language = group.CultureName,
            Status = group.Status,
            HasUnpublishedChanges = group.Status == PageStatuses.Published && group.HasChanges,
            MultilanguagePageId = group.MultilanguagePageId,
            Visibility = group.Visibility,
            UserGroups = group.UserGroups,
            OrganizationId = group.OrganizationId,
            StartDate = group.StartDate,
            EndDate = group.EndDate,
            CreatedDate = group.CreatedDate,
            CreatedBy = group.CreatedBy,
            ModifiedDate = group.ModifiedDate,
            ModifiedBy = group.ModifiedBy,
            ContentVersion = shownPage?.Status,
            VersionToken = snapshot.ETag,
            Settings = content?.Settings,
            Sections = content?.Sections,
            ContentNote = snapshot.Content == null
                ? "The page has no content in this version."
                : content == null ? "The stored content is not a page document." : null,
        };
    }
}
