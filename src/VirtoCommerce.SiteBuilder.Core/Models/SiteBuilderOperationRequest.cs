namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderOperationRequest
{
    public string StoreId { get; set; }

    /// <summary>What the caller sent that could not be read; the operation refuses it where it validates the request.</summary>
    public IList<string> BindingErrors { get; set; } = [];
}
