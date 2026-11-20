namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderFileSearchRequest : SiteBuilderThemeQueryRequest
{
    /// <summary>pages, blogs or themes.</summary>
    public string ContentType { get; set; }

    /// <summary>The folder to search in; <c>null</c> for the content folder itself.</summary>
    public string Folder { get; set; }

    /// <summary>Text the file name must contain, taken literally.</summary>
    public string Pattern { get; set; }

    /// <summary>What the blob storage searches the file names for.</summary>
    public string Keyword { get; set; }
}
