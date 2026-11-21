namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>What a preview code carries: whom the preview renders as, in which store, and the editor who asked for it.</summary>
public class PreviewAuthCodeTicket
{
    public string UserId { get; set; }

    public string StoreId { get; set; }

    public string OperatorUserId { get; set; }

    public string OperatorUserName { get; set; }

    public string Nonce { get; set; }
}
