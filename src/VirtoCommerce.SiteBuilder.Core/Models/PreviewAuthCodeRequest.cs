namespace VirtoCommerce.SiteBuilder.Core.Models;

public class PreviewAuthCodeRequest
{
    public string StoreId { get; set; }

    /// <summary>One of the store's preview users.</summary>
    public string UserId { get; set; }
}
