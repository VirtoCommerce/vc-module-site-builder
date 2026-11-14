using System.ComponentModel;
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
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// Pages of the stores the user reads: the pages list of the designer, one row per Landing Page and per Multilanguage
// Page, then the store's pages stored as files as its file tree lists a folder; and one page with its content.
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
    ISiteBuilderPublishStatusQueryService publishStatusQueryService,
    ILogger<PageTools> logger)
{
    // The pages list has no date filter: the tool finds the pages by their versions changed in the period first. More
    // versions than this is a scan; the agent narrows the period instead.
    private const int MaxChangedVersions = 1000;

    [McpServerTool(Name = "search_site_pages", ReadOnly = true, Destructive = false, Idempotent = true)]
    [McpPermission(Read)]
    [Description("Find pages of Site Builder. Returns one page of compact rows: a Landing Page is a row with its language; a " +
        "Multilanguage Page is one row with every language version (page id, status, unpublished changes, outdated " +
        "translation), most recently changed first. With store_id and no status, language or date filter, the store's pages " +
        "stored as files follow, with type File and their path as id ('pages/about.page'), which the other tools take with " +
        "store_id; page_type File or folder lists only them. Pass page_id to read one page instead: its settings (name, " +
        "address, language, audience, dates), its content (page settings and sections, each with its id) and the version " +
        "token of that content, which edit_page_draft takes; for a page stored as a file, also its publication state. " +
        "Pass section_type to find the database pages that use a section, optionally with a condition on its settings: the pages are " +
        "read one by one, take of them per call, and the answer lists per language version the matching section ids and " +
        "says how many pages were scanned; continue with next_skip until is_complete or has_more is false before saying a " +
        "section is used nowhere. To change the matching sections on every page, use update_sections_bulk.")]
    public async Task<object> SearchSitePages(
        RequestContext<CallToolRequestParams> context,
        IMcpPageSizeResolver pageSizeResolver,
        [Description("Store id. Omit it to search the database pages of every store you read; pages stored as files are listed per store.")] string store_id = null,
        [Description("Id of one page (a Landing Page, or a language version of a Multilanguage Page) to read with its " +
            "content, or the path of a page stored as a file with store_id. The other filters are ignored.")] string page_id = null,
        [Description("Text in the page name or address; for a page stored as a file, also in its path.")] string keyword = null,
        [Description("Draft (has unpublished changes or was never published), Published (has a published version) or " +
            "Archived. Several, comma-separated.")] string status = null,
        [Description("Language code, for example en-US: Landing Pages in it and Multilanguage Pages that have it.")] string language = null,
        [Description("Landing, Multilanguage or File (pages stored as files, with store_id). Omit it for all of them.")] string page_type = null,
        [Description("Pages stored as files of one folder: 'pages', 'blogs' or a folder inside them such as 'pages/landing'. " +
            "Its subfolders are not listed; without it, the pages right in 'pages' and 'blogs' are.")] string folder = null,
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

        var condition = ParseSectionCondition(section_type, setting_path, setting_equals, setting_contains, setting_language);
        var sources = SitePageSources.Parse(page_type, folder, store_id, HasDatabaseFilters(status, language, modified_since, modified_before));
        var paging = await pageSizeResolver.GetPagingAsync(skip, take);
        if (!sources.Database)
        {
            return await ListFilePagesAsync(context.User, store_id, sources.Folder, keyword, section_type, paging, cancellationToken);
        }

        var criteria = await CreateCriteriaAsync(context.User, store_id, keyword, status, language, sources.DatabasePageType, paging);
        if (modified_since != null || modified_before != null)
        {
            criteria.ObjectIds = await GetChangedPageIdsAsync(store_id, criteria.StoreIds, ToUtc(modified_since), ToUtc(modified_before));
            if (criteria.ObjectIds.Count == 0)
            {
                return NoPages(section_type, paging);
            }
        }

        if (!string.IsNullOrWhiteSpace(section_type))
        {
            return await SearchSectionsAsync(context, criteria, section_type.Trim(), condition, SitePageFilters.ParseVersion(version), cancellationToken);
        }

        return await ListAsync(context.User, criteria, paging, sources, keyword, cancellationToken);
    }

    private static SiteBuilderSectionSettingCondition ParseSectionCondition(string sectionType, string settingPath, string settingEquals,
        string settingContains, string settingLanguage)
    {
        var condition = SitePageFilters.ParseCondition(settingPath, settingEquals, settingContains, settingLanguage);
        if (condition != null && string.IsNullOrWhiteSpace(sectionType))
        {
            throw new McpException("A setting condition needs section_type: the type of the sections whose setting it compares.");
        }

        return condition;
    }

    // The filters only database pages answer: the list of pages stored as files has no status, language or dates.
    private static bool HasDatabaseFilters(string status, string language, DateTime? modifiedSince, DateTime? modifiedBefore) =>
        !string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace(language) || modifiedSince != null || modifiedBefore != null;

    private static object NoPages(string sectionType, McpPaging paging) =>
        string.IsNullOrWhiteSpace(sectionType) ? McpPage<SitePageRow>.Create([], 0, paging) : SitePageSectionSearch.Empty();

    private async Task<SiteBuilderPageSearchCriteria> CreateCriteriaAsync(ClaimsPrincipal user, string storeId, string keyword, string status,
        string language, string pageType, McpPaging paging)
    {
        var criteria = new SiteBuilderPageSearchCriteria
        {
            StoreId = storeId,
            Keyword = keyword,
            Statuses = SitePageFilters.ParseStatuses(status),
            LanguageCode = language,
            PageType = pageType,
            Sort = $"{nameof(SiteBuilderPageListItem.ModifiedDate)}:desc;{nameof(SiteBuilderPageListItem.CreatedDate)}:desc",
        }.WithPaging(paging);

        if (string.IsNullOrEmpty(storeId))
        {
            criteria.StoreIds = await authorizationService.GetStoreIdsAsync(user, userManagerFactory);
            if (criteria.StoreIds?.Count == 0)
            {
                throw new McpForbiddenException("Your builder:read permission does not name a store you work in, so there are no pages to search.");
            }
        }

        return criteria;
    }

    // The database pages of the filters, then the store's pages stored as files: a page takes the files where the
    // database pages run out, so next_skip walks both lists once. The files come as one list; the tool pages it.
    private async Task<McpPage<SitePageRow>> ListAsync(ClaimsPrincipal user, SiteBuilderPageSearchCriteria criteria, McpPaging paging,
        SitePageSources sources, string keyword, CancellationToken cancellationToken)
    {
        var result = await pageListService.SearchAsync(criteria, cancellationToken);
        if (!sources.Files)
        {
            return result.ToMcpPage(paging, SitePageRow.From);
        }

        var files = await FilePageList.ListAsync(contentQueryService, user, criteria.StoreId, sources.Folder, keyword, cancellationToken);
        var rows = result.Results.Select(SitePageRow.From).ToList();
        rows.AddRange(files.Skip(Math.Max(0, paging.Skip - result.TotalCount)).Take(paging.Take - rows.Count));

        return McpPage<SitePageRow>.Create(rows, result.TotalCount + files.Count, paging);
    }

    private async Task<McpPage<SitePageRow>> ListFilePagesAsync(ClaimsPrincipal user, string storeId, string folder, string keyword, string sectionType,
        McpPaging paging, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(sectionType))
        {
            throw new McpException("section_type scans database pages only. Read a page stored as a file with page_id for its sections.");
        }

        var files = await FilePageList.ListAsync(contentQueryService, user, storeId, folder, keyword, cancellationToken);

        return McpPage<SitePageRow>.FromList(files, paging);
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
        var content = await FilePages.ReadAsync(contentQueryService, logger, context.User, page, draft: !published, cancellationToken);
        if (content == null && (!published || await FilePages.ReadAsync(contentQueryService, logger, context.User, page, draft: true, cancellationToken) == null))
        {
            throw new McpException($"Page '{page.PageId}' was not found in store '{page.StoreId}'. Check the folder and path the designer shows for it.");
        }

        var status = await FilePages.RunAsync(logger, () => publishStatusQueryService.GetFilePageStatusAsync(new SiteBuilderFilePageQueryRequest
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

    // A page stored as a file: its id is the path the other tools take with store_id. The file list has no status,
    // language or dates; reading the page with page_id gives its publication state.
    public static SitePageRow FromFile(string storeId, string contentType, SiteBuilderFileSearchItem item) => new()
    {
        Id = new FilePage(storeId, contentType, item.Path?.Trim('/') ?? string.Empty).PageId,
        Type = SitePageSources.FileType,
        Name = item.Name,
        Permalink = item.PreviewUrl,
        StoreId = storeId,
    };
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
