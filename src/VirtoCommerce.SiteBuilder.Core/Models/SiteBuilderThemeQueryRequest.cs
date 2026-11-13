namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderThemeQueryRequest : SiteBuilderOperationRequest
{
    /// <summary>The theme folder; <c>null</c> for the store's theme.</summary>
    public string Theme { get; set; }
}
