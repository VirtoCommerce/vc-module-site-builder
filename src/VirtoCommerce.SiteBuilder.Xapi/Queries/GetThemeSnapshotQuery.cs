using VirtoCommerce.SiteBuilder.Xapi.Models;
using VirtoCommerce.Xapi.Core.Infrastructure;

namespace VirtoCommerce.SiteBuilder.Xapi.Queries;

public class GetThemeSnapshotQuery : IQuery<ThemeSnapshotResult>
{
    public string Domain { get; set; }

    public string StoreId { get; set; }

    public string CultureName { get; set; }

    public string Permalink { get; set; }
}
