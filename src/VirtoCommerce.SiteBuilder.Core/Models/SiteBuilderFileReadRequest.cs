namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderFileReadRequest : SiteBuilderThemeQueryRequest
{
    /// <summary>pages, blogs or themes.</summary>
    public string ContentType { get; set; }

    public string Path { get; set; }

    /// <summary>The draft when there is one, else what is published.</summary>
    public bool Draft { get; set; }

    /// <summary>The exact version of a page kept in git.</summary>
    public string Ref { get; set; }
}
