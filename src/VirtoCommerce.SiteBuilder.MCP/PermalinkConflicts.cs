using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;

namespace VirtoCommerce.SiteBuilder.MCP;

public static class PermalinkConflicts
{
    public const string Argument = "unpublish_conflicting";

    public static IList<SiteBuilderPermalinkUsage> Of(SiteBuilderOperationResult result) =>
        (result as SiteBuilderOperationResult<SiteBuilderPermalinkConflictResult>)?.Value?.Conflicts ?? [];

    // The publish-all dry run does not report the versions the write skips for a conflict; the dry run of each version does.
    public static async Task<IList<SiteBuilderPermalinkUsage>> FindPermalinkConflictsAsync(this IGroupedPageOperationService groupedPageOperationService,
        ClaimsPrincipal user, string groupId, CancellationToken cancellationToken)
    {
        return Of(await groupedPageOperationService.PublishAsync(new GroupedPagePublishOperationRequest
        {
            GroupId = groupId,
            Publish = true,
            SaveId = Guid.NewGuid().ToString("N"),
            DryRun = true,
        }, user, cancellationToken));
    }

    public static string Explain(SiteBuilderOperationResult result)
    {
        var conflicts = Of(result);

        return $"published pages serve the same permalink to the same visitors, and publishing would replace them: {Describe(conflicts)}. {Fix(conflicts)}";
    }

    public static string Describe(IEnumerable<SiteBuilderPermalinkUsage> conflicts) =>
        string.Join("; ", conflicts.Select(x => $"page '{x.GroupId}' ({x.Name}, {x.CultureName ?? "any language"})"));

    public static string FixSkippedVersion(string cultureName, IList<SiteBuilderPermalinkUsage> conflicts) =>
        $"Publish it on its own, with languages: ['{cultureName}']. {Fix(conflicts)}";

    public static string Fix(IList<SiteBuilderPermalinkUsage> conflicts)
    {
        var drafted = conflicts.Where(x => !x.CanUnpublish).ToList();

        return drafted.Count > 0
            ? $"A page with unpublished changes cannot be unpublished: publish or discard the draft of {Describe(drafted)} first (publish_page or " +
              $"discard_draft), then unpublish the pages or publish with {Argument}: true"
            : $"Unpublish them with unpublish_page first, or, if the user agrees to replace them, publish with {Argument}: true: its dry run " +
              "lists them, and the approved publication unpublishes them with the page";
    }
}
