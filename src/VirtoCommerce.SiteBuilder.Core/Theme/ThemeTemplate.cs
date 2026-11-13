namespace VirtoCommerce.SiteBuilder.Core.Theme;

/// <summary>A template of the theme, which lays out every page of a type that its rules give it.</summary>
public class ThemeTemplate
{
    /// <summary>E.g. "product" or "product.with-video".</summary>
    public string Key { get; set; }

    /// <summary>product, category or page.</summary>
    public string Type { get; set; }

    /// <summary>The template of the type, as against an alternate one that the template rules give some of its pages.</summary>
    public bool IsDefault { get; set; }

    /// <summary>The template has a live file, which the published theme holds.</summary>
    public bool Published { get; set; }

    /// <summary>The template has changes that are not published; without <see cref="Published"/> the draft is its only copy.</summary>
    public bool HasDraft { get; set; }
}
