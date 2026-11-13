namespace VirtoCommerce.SiteBuilder.Core.Models;

public class ContentHistoryListOperationRequest : ContentHistoryOperationRequest
{
    public string Cursor { get; set; }

    public int Take { get; set; } = ModuleConstants.GitHistory.DefaultPageSize;
}
