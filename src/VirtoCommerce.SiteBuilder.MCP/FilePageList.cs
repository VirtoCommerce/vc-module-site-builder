using System.Security.Claims;
using ModelContextProtocol;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.MCP.Tools;

namespace VirtoCommerce.SiteBuilder.MCP;

// The pages a list of search_site_pages takes: database pages, pages stored as files, or both. A page stored as a file
// is listed by the folder it is in, as the designer's file tree shows it, and has no status, language or date in that
// list, so a filter on them leaves the files out; asked for explicitly, the files refuse such a filter instead.
public sealed record SitePageSources(bool Database, bool Files, string DatabasePageType, string Folder)
{
    public const string FileType = "File";

    public static SitePageSources Parse(string pageType, string folder, string storeId, bool databaseFilters)
    {
        var files = FileType.EqualsIgnoreCase(pageType?.Trim());
        if (files || !string.IsNullOrWhiteSpace(folder))
        {
            return ParseFiles(files ? null : pageType, folder, storeId, databaseFilters);
        }

        var databasePageType = SitePageFilters.ParsePageType(pageType);

        // Without a filter only database pages have, the list of one store holds its pages stored as files too.
        var withFiles = databasePageType == null && !string.IsNullOrEmpty(storeId) && !databaseFilters;

        return new SitePageSources(Database: true, withFiles, databasePageType, Folder: null);
    }

    private static SitePageSources ParseFiles(string otherPageType, string folder, string storeId, bool databaseFilters)
    {
        if (!string.IsNullOrWhiteSpace(otherPageType))
        {
            throw new McpException("folder lists pages stored as files: with it, page_type is File or omitted.");
        }

        if (string.IsNullOrEmpty(storeId))
        {
            throw new McpException("Pages stored as files are listed per store: pass store_id.");
        }

        if (databaseFilters)
        {
            throw new McpException("Pages stored as files are listed without status, language and dates. Drop those filters, or read a page " +
                "with page_id for its publication state.");
        }

        return new SitePageSources(Database: false, Files: true, DatabasePageType: null, string.IsNullOrWhiteSpace(folder) ? null : folder.Trim());
    }
}

public static class FilePageList
{
    private const string Pages = "pages";
    private const string Blogs = "blogs";

    // The pages stored as files in the folder, its subfolders left out, as the designer lists one folder; without a
    // folder, those right in the pages and the blogs folders. keyword keeps the pages whose name, address or path holds it.
    public static async Task<IList<SitePageRow>> ListAsync(ISiteBuilderContentQueryService contentQueryService, ClaimsPrincipal user,
        string storeId, string folder, string keyword, CancellationToken cancellationToken)
    {
        var rows = new List<SitePageRow>();
        foreach (var (contentType, path) in ParseFolders(folder))
        {
            var result = await contentQueryService.SearchFilesAsync(new SiteBuilderFileSearchRequest
            {
                StoreId = storeId,
                ContentType = contentType,
                Folder = path,
            }, user, cancellationToken);

            rows.AddRange(ReadRows(result, storeId, contentType, folder));
        }

        return string.IsNullOrWhiteSpace(keyword) ? rows : rows.Where(x => Matches(x, keyword.Trim())).ToList();
    }

    private static IEnumerable<SitePageRow> ReadRows(SiteBuilderOperationResult<IList<SiteBuilderFileSearchItem>> result, string storeId,
        string contentType, string folder)
    {
        switch (result.Status)
        {
            case SiteBuilderOperationStatus.Succeeded:
                return (result.Value ?? []).Select(x => SitePageRow.FromFile(storeId, contentType, x));
            case SiteBuilderOperationStatus.Forbidden:
                throw new McpForbiddenException($"Your builder:read permission does not cover store '{storeId}'.");
            default:
                throw new McpException($"The pages of folder '{folder ?? contentType}' could not be listed: {result.Error}");
        }
    }

    // 'pages', 'blogs' or a folder inside one of them, as page_id names a page: the content folder and the path in it.
    private static IList<(string ContentType, string Path)> ParseFolders(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return [(Pages, null), (Blogs, null)];
        }

        var id = folder.Trim().Trim('/');
        var slash = id.IndexOf('/');
        var contentType = (slash < 0 ? id : id[..slash]).ToLowerInvariant();
        if (contentType is not (Pages or Blogs))
        {
            throw new McpException($"'{folder}' is not a folder of pages: use 'pages', 'blogs' or a folder inside them, for example 'pages/landing'.");
        }

        return [(contentType, slash < 0 ? null : id[(slash + 1)..])];
    }

    private static bool Matches(SitePageRow row, string keyword) =>
        (row.Name?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (row.Permalink?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
        row.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase);
}
