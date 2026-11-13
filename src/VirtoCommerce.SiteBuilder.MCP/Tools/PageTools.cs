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
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// Database pages of the stores the user reads: the pages list of the designer, one row per Landing Page and per
// Multilanguage Page, and one page with its content. A page stored as a file is read by its path; it is not listed, as
// listing the files of a store is the designer's endpoint's own work and not a service yet.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class PageTools(
    IAuthorizationService authorizationService,
    ISiteBuilderPageListService pageListService,
    ISiteBuilderPageSearchService pageVersionSearchService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService pageContentService,
    ISiteBuilderSectionScanner sectionScanner,
    Func<UserManager<ApplicationUser>> userManagerFactory,
    ISiteBuilderContentQueryService contentQueryService,
    ISiteBuilderPublishStatusQueryService publishStatusQueryService)
{
    // The pages list has no date filter: the tool finds the pages by their versions changed in the period first. More
    // versions than this is a scan; the agent narrows the period instead.
    private const int MaxChangedVersions = 1000;

    [McpServerTool(Name = "search_site_pages", ReadOnly = true, Destructive = false, Idempotent = true)]
    [McpPermission(Read)]
    [Description("Find database pages of Site Builder (pages created in the designer, not pages from files). Returns one " +
        "page of compact rows: a Landing Page is a row with its language; a Multilanguage Page is one row with every " +
        "language version (page id, status, unpublished changes, outdated translation). Most recently changed first. " +
        "Pass page_id to read one page instead: its settings (name, address, language, audience, dates), its content " +
        "(page settings and sections, each with its id) and the version token of that content, which edit_page_draft takes. " +
        "A page stored as a file is not listed here; read it with page_id set to its path ('pages/about.page') and store_id. " +
        "Pass section_type to find the pages that use a section, optionally with a condition on its settings: the pages are " +
        "read one by one, take of them per call, and the answer lists per language version the matching section ids and " +
        "says how many pages were scanned; continue with next_skip until is_complete or has_more is false before saying a " +
        "section is used nowhere. To change the matching sections on every page, use update_sections_bulk.")]
    public async Task<object> SearchSitePages(
        RequestContext<CallToolRequestParams> context,
        IMcpPageSizeResolver pageSizeResolver,
        [Description("Store id. Omit it to search every store you read pages of.")] string store_id = null,
        [Description("Id of one page (a Landing Page, or a language version of a Multilanguage Page) to read with its " +
            "content, or the path of a page stored as a file with store_id. The other filters are ignored.")] string page_id = null,
        [Description("Text in the page name or address.")] string keyword = null,
        [Description("Draft (has unpublished changes or was never published), Published (has a published version) or " +
            "Archived. Several, comma-separated.")] string status = null,
        [Description("Language code, for example en-US: Landing Pages in it and Multilanguage Pages that have it.")] string language = null,
        [Description("Landing or Multilanguage. Omit it for both.")] string page_type = null,
        [Description("Pages changed at or after this UTC time, ISO 8601. A change of a Shared Component on the page counts.")] DateTime? modified_since = null,
        [Description("Pages not changed after this UTC time, ISO 8601.")] DateTime? modified_before = null,
        [Description("With page_id or section_type: 'draft' (default) for the content the designer edits, which is the " +
            "published one when the page has no unpublished changes, or 'published' for what the storefront shows.")] string version = SitePageFilters.DraftVersion,
        [Description("Only pages with a section of this type, for example 'hero-banner', in the version 'version' names " +
            "(draft by default). A section that a placed Shared Component brings is listed with shared_component_id.")] string section_type = null,
        [Description("With section_type: a setting of the section to compare, a name or a dotted path such as 'title', " +
            "'button.url' or 'blocks.*.text' (* is any item of a list).")] string setting_path = null,
        [Description("With setting_path: the exact value the setting has. A text per language matches when one language's text does.")] string setting_equals = null,
        [Description("With setting_path: text the setting contains, ignoring case.")] string setting_contains = null,
        [Description("With setting_path: compare only this language's text of a setting that holds a text per language, for example fr-FR.")] string setting_language = null,
        [Description("Rows to skip, from 0. Use next_skip of the previous page. With section_type: the pages already scanned.")] int? skip = null,
        [Description("Rows to return. Omit it for the default page size. With section_type: the pages to scan.")] int? take = null,
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

        var condition = SitePageFilters.ParseCondition(setting_path, setting_equals, setting_contains, setting_language);
        if (condition != null && string.IsNullOrWhiteSpace(section_type))
        {
            throw new McpException("A setting condition needs section_type: the type of the sections whose setting it compares.");
        }

        var criteria = new SiteBuilderPageSearchCriteria
        {
            StoreId = store_id,
            Keyword = keyword,
            Statuses = SitePageFilters.ParseStatuses(status),
            LanguageCode = language,
            PageType = SitePageFilters.ParsePageType(page_type),
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
                return string.IsNullOrWhiteSpace(section_type) ? McpPage<SitePageRow>.Create([], 0, paging) : SitePageSectionSearch.Empty();
            }
        }

        if (!string.IsNullOrWhiteSpace(section_type))
        {
            return await SearchSectionsAsync(context, criteria, section_type.Trim(), condition, SitePageFilters.ParseVersion(version), cancellationToken);
        }

        var result = await pageListService.SearchAsync(criteria, cancellationToken);

        return result.ToMcpPage(paging, SitePageRow.From);
    }

    // The pages of the filters, read one by one from skip; take is how many are read in this call.
    private async Task<SitePageSectionSearch> SearchSectionsAsync(RequestContext<CallToolRequestParams> context, SiteBuilderPageSearchCriteria criteria,
        string sectionType, SiteBuilderSectionSettingCondition condition, bool published, CancellationToken cancellationToken)
    {
        var scan = await sectionScanner.ScanPagesAsync(new SiteBuilderSectionScanCriteria
        {
            Pages = criteria,
            MaxPages = criteria.Take,
            Published = published,
            SectionType = sectionType,
            Condition = condition,
            SharedComponentStoreIds = await authorizationService.GetStoreIdsAsync(context.User, userManagerFactory, criteria.StoreId, SharedComponentsRead),
        }, cancellationToken);

        return SitePageSectionSearch.From(scan);
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

    private async Task<object> GetFilePageAsync(RequestContext<CallToolRequestParams> context, string storeId, string pageId, bool published,
        CancellationToken cancellationToken)
    {
        var page = await FilePages.LoadAsync(authorizationService, context.User, storeId, pageId);
        var content = await FilePages.ReadAsync(contentQueryService, context.User, page, draft: !published, cancellationToken);
        if (content == null && (!published || await FilePages.ReadAsync(contentQueryService, context.User, page, draft: true, cancellationToken) == null))
        {
            throw new McpException($"Page '{page.PageId}' was not found in store '{page.StoreId}'. Check the folder and path the designer shows for it.");
        }

        var status = await FilePages.RunAsync(() => publishStatusQueryService.GetFilePageStatusAsync(new SiteBuilderFilePageQueryRequest
        {
            StoreId = page.StoreId,
            ContentType = page.ContentType,
            Path = page.Path,
        }, context.User, cancellationToken));

        return FilePageDetails.From(page, content, published, status.Succeeded ? status.Value : null);
    }

    private async Task<object> GetPageAsync(RequestContext<CallToolRequestParams> context, string storeId, string pageId,
        string version, CancellationToken cancellationToken)
    {
        var published = SitePageFilters.ParseVersion(version);
        if (PageLookup.IsFilePagePath(pageId))
        {
            return await GetFilePageAsync(context, storeId, pageId, published, cancellationToken);
        }

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

    // A source language version: when it was last published. A translation: the source publication it is based on.
    // set_translations takes the source's value as source_revision_date to mark a translation up to date.
    public DateTime? SourceRevisionDate { get; init; }

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
        var content = SiteBuilderPageDocument.Read(snapshot.Content);
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
            SourceRevisionDate = group.SourceRevisionDate,
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

public sealed class FilePageDetails
{
    // The path with its folder, as page_id of the tools takes it.
    public string Id { get; init; }

    public string StoreId { get; init; }

    // pages or blogs.
    public string ContentType { get; init; }

    public string Path { get; init; }

    // git or blob: in git, a publish is a pull request.
    public string Flow { get; init; }

    public bool? Published { get; init; }

    public bool? HasUnpublishedChanges { get; init; }

    // In git: a pull request of the page is open; it waits for checks or for a person to merge it.
    public bool? PublishPending { get; init; }

    // draft or published: the version the content below is of. The draft of a store in git is your own draft branch.
    public string ContentVersion { get; init; }

    // The hash of the content: edit_page_draft and restore_version refuse it once the content changed.
    public string VersionToken { get; init; }

    public JsonObject Settings { get; init; }

    public JsonArray Sections { get; init; }

    public string ContentNote { get; init; }

    public static FilePageDetails From(FilePage page, string content, bool published, PagePublishStatus status)
    {
        var document = content == null ? null : SiteBuilderPageDocument.Read(content);

        return new FilePageDetails
        {
            Id = page.PageId,
            StoreId = page.StoreId,
            ContentType = page.ContentType,
            Path = page.Path,
            Flow = status?.Flow,
            Published = status?.Published,
            HasUnpublishedChanges = status?.HasChanges,
            PublishPending = status == null ? null : status.Pending || status.AwaitingMerge,
            ContentVersion = published ? "published" : "draft",
            VersionToken = FilePages.VersionOf(content),
            Settings = document?.Settings,
            Sections = document?.Sections,
            ContentNote = content == null
                ? "The page is not published."
                : document == null ? "The stored content is not a page document." : null,
        };
    }
}
