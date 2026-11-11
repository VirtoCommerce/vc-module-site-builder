using GraphQL.Resolvers;
using GraphQL.Types;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Xapi.Queries;
using VirtoCommerce.Xapi.Core.Extensions;
using VirtoCommerce.Xapi.Core.Helpers;
using VirtoCommerce.Xapi.Core.Infrastructure;

namespace VirtoCommerce.SiteBuilder.Xapi.Schemas;

/// <summary>Anonymous, because the theme is the same for every visitor.</summary>
public class ThemeSnapshotSchema : ISchemaBuilder
{
    public void Build(ISchema schema)
    {
        _ = schema.Query.AddField(new FieldType
        {
            Name = "themeSnapshot",
            Arguments = new QueryArguments(
                new QueryArgument<StringGraphType> { Name = "domain" },
                new QueryArgument<StringGraphType> { Name = "storeId" },
                new QueryArgument<StringGraphType> { Name = "cultureName" },
                new QueryArgument<StringGraphType> { Name = "permalink" }
            ),
            Type = GraphTypeExtensionHelper.GetActualType<ThemeSnapshotType>(),
            Resolver = new FuncFieldResolver<object>(async context =>
            {
                var query = AbstractTypeFactory<GetThemeSnapshotQuery>.TryCreateInstance();
                query.Domain = context.GetArgument<string>("domain");
                query.StoreId = context.GetArgument<string>("storeId");
                query.CultureName = context.GetArgument<string>("cultureName");
                query.Permalink = context.GetArgument<string>("permalink");

                return await context.GetMediator().Send(query);
            }),
        });
    }
}
