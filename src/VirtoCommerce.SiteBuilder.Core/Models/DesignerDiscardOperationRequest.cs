namespace VirtoCommerce.SiteBuilder.Core.Models;

public class DesignerDiscardOperationRequest : SiteBuilderOperationRequest
{
    public DesignerDiscardRequest Changes { get; set; }

    public string SaveId { get; set; }
}
