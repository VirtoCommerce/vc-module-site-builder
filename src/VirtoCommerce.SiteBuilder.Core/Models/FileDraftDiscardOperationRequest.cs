namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A draft in blob storage to discard: of a page or a blog article, or of a theme file with the themes content type.</summary>
public class FileDraftDiscardOperationRequest : FilePageOperationRequest
{
    /// <summary>The theme folder; <c>null</c> for the store's theme.</summary>
    public string Theme { get; set; }

    public string SaveId { get; set; }

    /// <summary>
    /// The version of the draft the caller reviewed, as a dry run answers it in <see cref="SiteBuilderObjectChange.ETag"/>: the discard is refused
    /// once the draft is at another one, or gone. <c>null</c> discards the draft whatever it holds.
    /// </summary>
    public string ExpectedDraftVersion { get; set; }
}
