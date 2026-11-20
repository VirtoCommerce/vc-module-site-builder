using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>
/// Status transitions of a grouped page shared by single-page and batch (multilanguage) actions.
/// </summary>
public static class GroupedPagePublishing
{
    /// <summary>
    /// Publishes the current draft. The other pages of the group become obsolete and must be deleted by the caller after saving the group.
    /// </summary>
    /// <returns><c>false</c> when the group has no draft; the group is left unchanged.</returns>
    public static bool TryPublishDraft(GroupedSiteBuilderPage group, out IList<string> obsoletePageIds)
    {
        var draft = GetDraft(group);
        if (draft == null)
        {
            obsoletePageIds = [];
            return false;
        }

        draft.Status = Published;
        obsoletePageIds = group.Pages.Where(x => x.Id != draft.Id).Select(x => x.Id).ToList();
        return true;
    }

    /// <summary>The draft a publish makes live; <c>null</c> when the group has none.</summary>
    public static SiteBuilderPage GetDraft(GroupedSiteBuilderPage group) =>
        SiteBuilderPageSelection.Order(group.Pages).FirstOrDefault(x => x.Status == Draft);

    /// <returns><c>false</c> when every page is already archived.</returns>
    public static bool Archive(GroupedSiteBuilderPage group)
    {
        var changed = false;

        foreach (var page in group.Pages.Where(x => x.Status != Archived))
        {
            page.Status = Archived;
            changed = true;
        }

        return changed;
    }
}
