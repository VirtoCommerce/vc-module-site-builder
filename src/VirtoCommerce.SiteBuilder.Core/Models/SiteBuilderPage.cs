using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderPage : AuditableEntity, IHasStoreId, ICloneable
{
    public string GroupId { get; set; }

    public string StoreId { get; set; }
    public string Status { get; set; } // Draft | Published | Archived

    // Transient write-only field used at create time to atomically persist page content
    // alongside the page row (via EF table-splitting on SiteBuilderContentEntity).
    // Always null on read paths; use IGroupedPageService.LoadContent to retrieve content.
    public string Content { get; set; }

    public object Clone()
    {
        return MemberwiseClone();
    }
}
