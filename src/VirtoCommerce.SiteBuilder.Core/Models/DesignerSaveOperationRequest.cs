namespace VirtoCommerce.SiteBuilder.Core.Models;

public class DesignerSaveOperationRequest : SiteBuilderOperationRequest
{
    public DesignerSaveRequest Changes { get; set; }

    /// <summary>The section schema catalog the designer previewed; theme files are checked against it.</summary>
    public string CatalogHash { get; set; }

    public string SaveId { get; set; }

    /// <summary>Checks and sanitizes the changes as a save does, and answers what it would write instead of writing it.</summary>
    public bool DryRun { get; set; }
}
