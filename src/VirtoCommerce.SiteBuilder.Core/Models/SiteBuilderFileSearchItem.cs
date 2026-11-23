namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A page found by a file search, with the name and permalink its settings give it.</summary>
public class SiteBuilderFileSearchItem
{
    /// <summary>What the search answer lists the page by: the content type and the file's path.</summary>
    public string Key { get; set; }

    public string Name { get; set; }

    public string PreviewUrl { get; set; }

    public string Path { get; set; }
}
