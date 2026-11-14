using VirtoCommerce.SiteBuilder.Core.History;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class ContentRestoreCommand
{
    public string StoreId { get; set; }

    public string VersionId { get; set; }

    public string SaveId { get; set; }

    /// <summary>The caller has already checked access to it.</summary>
    public GroupedSiteBuilderPage Group { get; set; }

    public IDictionary<HistoryItem, string> Contents { get; set; } = new Dictionary<HistoryItem, string>();

    /// <summary>The version each draft has to be at for the restore to overwrite it; none, or <c>null</c>, overwrites the drafts whatever they hold.</summary>
    public IDictionary<HistoryItem, string> ETags { get; set; }
}
