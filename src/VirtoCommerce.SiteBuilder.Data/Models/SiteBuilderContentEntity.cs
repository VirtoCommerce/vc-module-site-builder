using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Data.Models;

public class SiteBuilderContentEntity : Entity
{
    public string PageContent { get; set; }

    public virtual SiteBuilderPageEntity Page { get; set; }
}
