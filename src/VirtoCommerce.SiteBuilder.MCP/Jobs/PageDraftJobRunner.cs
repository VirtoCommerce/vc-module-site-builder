using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;

namespace VirtoCommerce.SiteBuilder.MCP.Jobs;

public enum PageJobOutcome
{
    Changed,
    Unchanged,
    Skipped,
    Refused,
    Failed,
}

public sealed record PageJobResult(PageJobOutcome Outcome, string Reason = null, IList<PageDocumentChange> Changes = null, string VersionToken = null);

// One page of a bulk tool: the edit applied to the draft the tool saw, saved through the designer's own save as the
// user. A page that changed since the tool read it is skipped rather than overwritten.
public class PageDraftJobRunner(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService pageContentService,
    IDesignerOperationService designerOperationService)
{
    public const string ChangedSince = "changed since the call";

    public virtual async Task<PageJobResult> RunAsync(ClaimsPrincipal user, string pageId, string versionToken, IReadOnlyList<PageEditOperation> operations,
        bool dryRun, CancellationToken cancellationToken)
    {
        var group = string.IsNullOrEmpty(pageId) ? null : await groupedPageService.GetByIdAsync(pageId);
        if (group == null)
        {
            return new(PageJobOutcome.Failed, "the page was deleted");
        }

        if (!await authorizationService.IsAllowedAsync(user, group))
        {
            return new(PageJobOutcome.Refused, $"no builder:read in store '{group.StoreId}'");
        }

        SiteBuilderContentSnapshot snapshot;
        try
        {
            snapshot = await pageContentService.LoadGroupContentAsync(group, draft: true, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            return new(PageJobOutcome.Failed, "the page was deleted");
        }

        if (snapshot.ETag != versionToken)
        {
            return new(PageJobOutcome.Skipped, ChangedSince);
        }

        var editor = PageDocumentEditor.Read(snapshot.Content);
        var before = PageDocumentEditor.Read(snapshot.Content);
        if (editor == null)
        {
            return new(PageJobOutcome.Failed, "its content is not a page document");
        }

        try
        {
            await editor.ApplyAsync(operations, NoSources.Instance, cancellationToken);
        }
        catch (McpException ex)
        {
            return new(PageJobOutcome.Failed, ex.Message);
        }

        var changes = PageDocumentChanges.Compare(before, editor);
        if (changes.Count == 0)
        {
            return new(PageJobOutcome.Unchanged, Changes: changes, VersionToken: snapshot.ETag);
        }

        var result = await designerOperationService.SaveAsync(new DesignerSaveOperationRequest
        {
            StoreId = group.StoreId,
            Changes = new DesignerSaveRequest
            {
                Page = new DesignerSavePage { GroupId = group.Id, ETag = versionToken, Content = editor.Write() },
            },
            SaveId = Guid.NewGuid().ToString("N"),
            DryRun = dryRun,
        }, user, cancellationToken);

        return result.Status switch
        {
            SiteBuilderOperationStatus.Succeeded => new(PageJobOutcome.Changed, Changes: changes, VersionToken: dryRun ? versionToken : result.Value?.ETag),
            SiteBuilderOperationStatus.Forbidden => new(PageJobOutcome.Refused, $"no access to the page in store '{group.StoreId}'"),
            SiteBuilderOperationStatus.Conflict or SiteBuilderOperationStatus.VersionRequired => new(PageJobOutcome.Skipped, ChangedSince),
            SiteBuilderOperationStatus.NotFound => new(PageJobOutcome.Failed, "the page was deleted"),
            _ => new(PageJobOutcome.Failed, $"not saved: {string.Join(" ", new[] { result.Error }.Concat(result.Errors?.Skip(1) ?? []).Where(x => !string.IsNullOrEmpty(x)))}"),
        };
    }

    // The bulk changes read nothing outside the page.
    private sealed class NoSources : IPageEditSources
    {
        public static readonly NoSources Instance = new();

        public Task<JsonArray> GetPageSectionsAsync(string pageId, CancellationToken cancellationToken) => throw Unsupported();

        public Task EnsureSharedComponentAsync(string sharedComponentId, CancellationToken cancellationToken) => throw Unsupported();

        public Task<JsonArray> GetSharedComponentSectionsAsync(string sharedComponentId, CancellationToken cancellationToken) => throw Unsupported();

        private static McpException Unsupported() => new("A bulk change cannot copy sections or place Shared Components; use edit_page_draft.");
    }
}

// The outcome of every page of a job, and the job's final message: done, skipped, refused and failed, with the pages.
public sealed class PageJobTally
{
    private const int MaxNamedPages = 10;

    private readonly List<(string PageId, PageJobResult Result)> _results = [];

    public int Total => _results.Count;

    public int Count(PageJobOutcome outcome) => _results.Count(x => x.Result.Outcome == outcome);

    public void Add(string pageId, PageJobResult result) => _results.Add((pageId, result));

    public string Describe(string done = "changed")
    {
        var parts = new List<string> { $"{Count(PageJobOutcome.Changed)} pages {done}" };
        AddPart(parts, PageJobOutcome.Unchanged, "unchanged (nothing to change)");
        AddPart(parts, PageJobOutcome.Skipped, "skipped because they changed since the call");
        AddPart(parts, PageJobOutcome.Refused, "refused");
        AddPart(parts, PageJobOutcome.Failed, "failed");

        return string.Join("; ", parts) + ".";
    }

    private void AddPart(List<string> parts, PageJobOutcome outcome, string label)
    {
        var pages = _results.Where(x => x.Result.Outcome == outcome).ToList();
        if (pages.Count == 0)
        {
            return;
        }

        var named = pages.Take(MaxNamedPages).Select(x => x.Result.Reason == null || x.Result.Reason == PageDraftJobRunner.ChangedSince
            ? x.PageId
            : $"{x.PageId} ({x.Result.Reason})");
        var more = pages.Count > MaxNamedPages ? $" and {pages.Count - MaxNamedPages} more" : string.Empty;
        parts.Add($"{pages.Count} {label}: {string.Join(", ", named)}{more}");
    }
}
