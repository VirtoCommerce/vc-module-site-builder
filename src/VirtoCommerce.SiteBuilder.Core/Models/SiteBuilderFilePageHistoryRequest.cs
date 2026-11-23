namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderFilePageHistoryRequest : SiteBuilderFilePageQueryRequest
{
    /// <summary>How far back to read the production branch; <c>null</c> for the configured depth.</summary>
    public int? Take { get; set; }

    /// <summary>The cursor of a previous answer, to go on with the branches it did not look at.</summary>
    public string After { get; set; }
}
