namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SharedComponentUpdateOperationRequest : SharedComponentOperationRequest
{
    /// <summary>A component stays in its store: another store is refused; <c>null</c> keeps it.</summary>
    public string StoreId { get; set; }

    public string Name { get; set; }
}
