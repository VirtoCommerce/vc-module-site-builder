using Microsoft.AspNetCore.Authorization;
using VirtoCommerce.SiteBuilder.Xapi.Models;
using VirtoCommerce.SiteBuilder.Xapi.Schemas;
using VirtoCommerce.Xapi.Core.BaseQueries;

namespace VirtoCommerce.SiteBuilder.Xapi.Queries;

/// <summary>Anonymous, because the theme is the same for every visitor.</summary>
public class GetThemeSnapshotQueryBuilder(IAuthorizationService authorizationService)
    : QueryBuilder<GetThemeSnapshotQuery, ThemeSnapshotResult, ThemeSnapshotType>(authorizationService)
{
    protected override string Name => "themeSnapshot";
}
