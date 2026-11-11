using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderSharedComponent : AuditableEntity, IHasStoreId, ICloneable
{
    public string StoreId { get; set; }

    public string Name { get; set; }

    public int UsageCount { get; set; }

    public IList<SiteBuilderSharedComponentUsagePage> UsagePages { get; set; } = [];

    public object Clone()
    {
        var clone = (SiteBuilderSharedComponent)MemberwiseClone();
        clone.UsagePages = UsagePages
            .Select(x => new SiteBuilderSharedComponentUsagePage
            {
                Id = x.Id,
                Name = x.Name,
                Permalink = x.Permalink,
                CultureName = x.CultureName,
                Status = x.Status,
            })
            .ToList();

        return clone;
    }
}
