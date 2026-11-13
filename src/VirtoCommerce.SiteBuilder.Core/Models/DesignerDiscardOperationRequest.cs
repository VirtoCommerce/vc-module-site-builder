namespace VirtoCommerce.SiteBuilder.Core.Models;

public class DesignerDiscardOperationRequest : SiteBuilderOperationRequest
{
    public DesignerDiscardRequest Changes { get; set; }

    public string SaveId { get; set; }

    /// <summary>Checks the discard as it would run and answers the drafts it would throw away instead of throwing them away.</summary>
    public bool DryRun { get; set; }
}
