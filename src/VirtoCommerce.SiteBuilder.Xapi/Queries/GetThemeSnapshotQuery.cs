using System.Collections.Generic;
using GraphQL;
using GraphQL.Types;
using VirtoCommerce.SiteBuilder.Xapi.Models;
using VirtoCommerce.Xapi.Core.BaseQueries;

namespace VirtoCommerce.SiteBuilder.Xapi.Queries;

public class GetThemeSnapshotQuery : Query<ThemeSnapshotResult>
{
    public string Domain { get; set; }

    public string StoreId { get; set; }

    public string CultureName { get; set; }

    public string Permalink { get; set; }

    public override IEnumerable<QueryArgument> GetArguments()
    {
        yield return Argument<StringGraphType>(nameof(Domain));
        yield return Argument<StringGraphType>(nameof(StoreId));
        yield return Argument<StringGraphType>(nameof(CultureName));
        yield return Argument<StringGraphType>(nameof(Permalink));
    }

    public override void Map(IResolveFieldContext context)
    {
        Domain = context.GetArgument<string>(nameof(Domain));
        StoreId = context.GetArgument<string>(nameof(StoreId));
        CultureName = context.GetArgument<string>(nameof(CultureName));
        Permalink = context.GetArgument<string>(nameof(Permalink));
    }
}
