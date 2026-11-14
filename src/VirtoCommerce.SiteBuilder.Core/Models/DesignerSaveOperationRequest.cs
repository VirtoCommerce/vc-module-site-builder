namespace VirtoCommerce.SiteBuilder.Core.Models;

public class DesignerSaveOperationRequest : SiteBuilderOperationRequest
{
    public DesignerSaveRequest Changes { get; set; }

    /// <summary>The section schema catalog the designer previewed; theme files are checked against it.</summary>
    public string CatalogHash { get; set; }

    public string SaveId { get; set; }

    /// <summary>Checks and sanitizes the changes as a save does, and answers what it would write instead of writing it.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// For a page or a blog article kept as a file: the version of the draft the save replaces, as a dry run tells it in
    /// <see cref="SiteBuilderObjectChange.ETag"/> (<see cref="SiteBuilderContentVersion.CreateForFileContent"/> of the draft as it is read). The save is
    /// refused once the draft changed; <c>null</c> saves over whatever it holds. A database page is versioned by <see cref="DesignerSavePage.ETag"/>.
    /// </summary>
    public string ExpectedFilePageVersion { get; set; }
}
