using VirtoCommerce.SiteBuilder.Core.Theme;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class DesignerSaveResult : ContentSaveResult
{
    public string ETag { get; set; }

    public IList<ThemeFileVersion> ThemeFiles { get; set; } = [];
}
