namespace VirtoCommerce.SiteBuilder.Core.Models;

public class FilePageRestoreVersionOperationRequest : FilePageOperationRequest
{
    /// <summary>The commit of the version to continue editing from.</summary>
    public string Sha { get; set; }
}
