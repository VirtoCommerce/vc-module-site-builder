namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>Copies the content of another page into the draft of the page named by <see cref="GroupedPageOperationRequest.GroupId"/>.</summary>
public class GroupedPageContentCopyOperationRequest : GroupedPageOperationRequest
{
    public string SourceGroupId { get; set; }
}
