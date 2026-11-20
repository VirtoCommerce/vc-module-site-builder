namespace VirtoCommerce.SiteBuilder.Core.Theme;

/// <summary>What a publication of the theme would put live, checked as the publication checks it.</summary>
public class ThemePublicationPreview
{
    /// <summary>Each draft by its path with the theme folder, at its version.</summary>
    public IList<ThemeFileVersion> Drafts { get; set; } = [];

    /// <summary>What goes live as it is without being refused, such as a live palette that fails the contrast the storefront needs.</summary>
    public IList<string> Warnings { get; set; } = [];
}
