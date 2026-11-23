namespace VirtoCommerce.SiteBuilder.Xapi.Models;

/// <summary>Lets the storefront choose the page's layout before the page itself loads.</summary>
public class ThemeSnapshotSlug
{
    public string ObjectType { get; set; }

    public string ObjectId { get; set; }

    public ThemeSnapshotSlugPage Page { get; set; }
}
