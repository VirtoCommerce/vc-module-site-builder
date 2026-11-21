using VirtoCommerce.SiteBuilder.Core.Theme;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class DesignerSaveResult : ContentSaveResult
{
    public string ETag { get; set; }

    public IList<ThemeFileVersion> ThemeFiles { get; set; } = [];

    /// <summary>What was saved as it is but the author should know, such as text a custom section's template holds in one language only.</summary>
    public IList<string> Warnings { get; set; } = [];
}
