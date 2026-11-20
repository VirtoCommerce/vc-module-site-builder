using System.Collections.Generic;
using VirtoCommerce.XCMS.Core.Models;

namespace VirtoCommerce.SiteBuilder.Xapi.Models;

public class ThemeSnapshotResult
{
    public string StoreId { get; set; }

    public string CultureName { get; set; }

    public int Publication { get; set; }

    public string Content { get; set; }

    public IList<Menu> Menus { get; set; } = [];

    public ThemeSnapshotSlug Slug { get; set; }
}
