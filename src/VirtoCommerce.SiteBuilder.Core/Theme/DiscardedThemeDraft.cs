using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Theme;

public class DiscardedThemeDraft
{
    /// <summary>Path in the store's theme storage, e.g. "default/templates/product.json".</summary>
    public string Path { get; set; }

    public DraftDiscardStatus Status { get; set; }

    public string PublishedContent { get; set; }
}
