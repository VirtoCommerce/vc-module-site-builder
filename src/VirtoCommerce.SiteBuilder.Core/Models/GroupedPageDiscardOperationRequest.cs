namespace VirtoCommerce.SiteBuilder.Core.Models;

public class GroupedPageDiscardOperationRequest : GroupedPageOperationRequest
{
    public string SaveId { get; set; }

    /// <summary>
    /// The version of the draft the caller reviewed, as a dry run answers it in <see cref="SiteBuilderObjectChange.ETag"/>: the discard is refused
    /// once the draft is at another one. <c>null</c> discards the draft whatever it holds.
    /// </summary>
    public string ExpectedDraftVersion { get; set; }
}
