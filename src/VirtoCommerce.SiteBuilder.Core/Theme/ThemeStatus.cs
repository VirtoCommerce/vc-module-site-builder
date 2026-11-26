namespace VirtoCommerce.SiteBuilder.Core.Theme;

public class ThemeStatus
{
    public int? Publication { get; set; }

    /// <summary>By their path in the theme, e.g. "templates/product.json".</summary>
    public IList<string> Drafts { get; set; } = [];

    /// <summary>
    /// Those of <see cref="Drafts"/> with no live file: the draft is the file's only copy, so a Discard keeps it, but for a custom section type's,
    /// whose Discard removes the type.
    /// </summary>
    public IList<string> NewDrafts { get; set; } = [];
}
