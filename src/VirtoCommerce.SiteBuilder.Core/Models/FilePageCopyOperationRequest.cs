namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A copy of the page at <see cref="FilePageOperationRequest.Path"/>.</summary>
public class FilePageCopyOperationRequest : FilePageOperationRequest
{
    /// <summary>The path of the copy; <c>null</c> for the first free one beside the page.</summary>
    public string TargetPath { get; set; }
}
