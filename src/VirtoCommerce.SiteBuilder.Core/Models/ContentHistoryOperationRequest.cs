namespace VirtoCommerce.SiteBuilder.Core.Models;

public class ContentHistoryOperationRequest : SiteBuilderOperationRequest
{
    /// <summary><c>page:{groupId}</c> or <c>theme:{path}</c>.</summary>
    public IList<string> Items { get; set; } = [];

    /// <summary>A request with a version names at most one database page.</summary>
    public string VersionId { get; set; }
}
