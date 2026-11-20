namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>
/// The 409 response to publishing a page while other published pages have its permalink for the same visitors.
/// </summary>
public class SiteBuilderPermalinkConflictResult
{
    public string Error { get; set; }

    public IList<SiteBuilderPermalinkUsage> Conflicts { get; set; } = [];
}
