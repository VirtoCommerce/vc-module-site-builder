namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderSharedComponentUsage
{
    public string SharedComponentId { get; set; }

    public int UsageCount { get; set; }

    public IList<SiteBuilderSharedComponentUsagePage> Pages { get; set; } = [];
}
