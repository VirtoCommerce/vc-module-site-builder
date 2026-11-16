namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>
/// Another page of the same store that uses the same permalink.
/// </summary>
public class SiteBuilderPermalinkUsage
{
    public string GroupId { get; set; }

    public string Name { get; set; }

    public string Permalink { get; set; }

    public string CultureName { get; set; }

    public string Status { get; set; }

    /// <summary>
    /// The conflict with the checked page if both are published.
    /// </summary>
    public SiteBuilderPermalinkConflict Conflict { get; set; }

    /// <summary>
    /// False when the page has unpublished changes, so it cannot be unpublished to resolve the conflict.
    /// </summary>
    public bool CanUnpublish { get; set; }
}
