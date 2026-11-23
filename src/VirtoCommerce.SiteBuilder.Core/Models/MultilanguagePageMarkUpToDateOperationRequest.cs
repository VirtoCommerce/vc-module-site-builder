namespace VirtoCommerce.SiteBuilder.Core.Models;

public class MultilanguagePageMarkUpToDateOperationRequest : GroupedPageOperationRequest
{
    /// <summary>
    /// The source revision the caller reviewed, as a dry run tells it in the <c>sourceRevisionDate</c> field it changes to: the write is refused
    /// once the source language version was published again, as read under the lock of the save. <c>null</c> takes the source revision as it is then.
    /// </summary>
    public DateTime? ExpectedSourceRevisionDate { get; set; }
}
