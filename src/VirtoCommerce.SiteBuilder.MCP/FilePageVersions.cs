using System.Globalization;
using System.Security.Claims;
using ModelContextProtocol;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.MCP.Tools;

namespace VirtoCommerce.SiteBuilder.MCP;

// The versions of a page stored as a file in git, as the designer's history panel shows them: the unpublished commits
// on draft branches first, then the published ones on the production branch. One answer of the history covers a page of
// branches; its cursor reads the next page of branches, which lists the production branch again. The tool's cursor
// holds both the branch cursor and how far into that answer the agent has read.
public static class FilePageVersions
{
    private const string CursorPrefix = "file:";
    private const char CursorSeparator = ':';

    public static async Task<ContentVersionsAnswer> ListAsync(ISiteBuilderContentQueryService contentQueryService, ClaimsPrincipal user, FilePage page,
        string cursor, int take, CancellationToken cancellationToken)
    {
        var (offset, after) = ParseCursor(cursor);
        var result = await FilePages.RunAsync(() => contentQueryService.GetFilePageHistoryAsync(new SiteBuilderFilePageHistoryRequest
        {
            StoreId = page.StoreId,
            ContentType = page.ContentType,
            Path = page.Path,
            After = after,
        }, user, cancellationToken));

        switch (result.Status)
        {
            case SiteBuilderOperationStatus.Succeeded:
                return Answer(page, result.Value ?? PageHistory.Empty, offset, after, take);
            case SiteBuilderOperationStatus.NotFound:
                return NoHistory(page);
            case SiteBuilderOperationStatus.Forbidden:
                throw new McpForbiddenException(FilePages.Forbidden(page, "read the versions of"));
            default:
                throw new McpException($"The versions of page '{page.PageId}' could not be read: {result.Error}");
        }
    }

    private static ContentVersionsAnswer Answer(FilePage page, PageHistory history, int offset, string after, int take)
    {
        // A later page of branches lists the production branch again; its versions were on the first page.
        var versions = after == null ? history.Versions : history.Versions.Where(x => !x.Published).ToList();

        return new ContentVersionsAnswer
        {
            StoreId = page.StoreId,
            History = "enabled",
            HistoryAvailable = true,
            Note = history.Truncated
                ? "The repository has more branches than this answer looked at: next_cursor reads the unpublished versions on the others."
                : null,
            Versions = versions.Skip(offset).Take(take).Select(x => ContentVersionRow.From(x, page)).ToList(),
            NextCursor = GetNextCursor(history, versions.Count, offset + take, after),
        };
    }

    private static string GetNextCursor(PageHistory history, int count, int next, string after)
    {
        if (next < count)
        {
            return Cursor(next, after);
        }

        return history.Truncated && !string.IsNullOrEmpty(history.EndCursor) ? Cursor(0, history.EndCursor) : null;
    }

    // In blob storage a save overwrites the page in place, so there is nothing to list and nothing to restore.
    private static ContentVersionsAnswer NoHistory(FilePage page) => new()
    {
        StoreId = page.StoreId,
        History = "unavailable",
        HistoryAvailable = false,
        Note = $"Store '{page.StoreId}' keeps its pages stored as files in blob storage, where a save overwrites the page: '{page.PageId}' " +
            "has no versions to list or restore.",
        Versions = [],
    };

    private static string Cursor(int offset, string after) =>
        $"{CursorPrefix}{offset.ToString(CultureInfo.InvariantCulture)}{CursorSeparator}{after}";

    private static (int Offset, string After) ParseCursor(string cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (0, null);
        }

        var parts = cursor.StartsWith(CursorPrefix, StringComparison.Ordinal) ? cursor[CursorPrefix.Length..].Split(CursorSeparator, 2) : [];
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            throw new McpException("The cursor does not belong to this history. Call list_versions without cursor.");
        }

        return (offset, parts[1].Length == 0 ? null : parts[1]);
    }
}
