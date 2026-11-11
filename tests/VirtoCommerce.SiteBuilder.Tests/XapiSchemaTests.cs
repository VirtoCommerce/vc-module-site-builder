using System.Linq;
using GraphQL;
using GraphQL.Introspection;
using GraphQL.MicrosoftDI;
using GraphQL.Types;
using GraphQL.Utilities;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Xapi;
using VirtoCommerce.SiteBuilder.Xapi.Extensions;
using VirtoCommerce.SiteBuilder.Xapi.Models;
using VirtoCommerce.SiteBuilder.Xapi.Queries;
using VirtoCommerce.SiteBuilder.Xapi.Schemas;
using VirtoCommerce.Xapi.Core.Extensions;
using VirtoCommerce.Xapi.Core.Helpers;
using VirtoCommerce.Xapi.Core.Infrastructure;
using VirtoCommerce.XCMS.Core;
using VirtoCommerce.XCMS.Core.Models;
using VirtoCommerce.XCMS.Core.Queries;
using VirtoCommerce.XCMS.Core.Schemas;
using VirtoCommerce.XCMS.Data;
using VirtoCommerce.XCMS.Data.Schemas;
using Xunit;
using PageBuilderGroupedPageService = VirtoCommerce.PageBuilderModule.Core.Services.IGroupedPageService;

namespace VirtoCommerce.SiteBuilder.Tests;

/// <summary>Registers x-cms first and Site Builder after it, as the module load order does.</summary>
public class XapiSchemaTests
{
    [Fact]
    public void BuilderPageQuery_IsHandledBySiteBuilder()
    {
        var services = CreateServices();

        var handler = Assert.Single(services, x => x.ServiceType == typeof(IRequestHandler<GetBuilderPageQuery, BuilderPageItem>));
        Assert.Equal(typeof(GetSiteBuilderPreviewPageQueryHandler), handler.ImplementationType);
    }

    [Fact]
    public void ThemeSnapshotQuery_IsHandledBySiteBuilder()
    {
        var services = CreateServices();

        var handler = Assert.Single(services, x => x.ServiceType == typeof(IRequestHandler<GetThemeSnapshotQuery, ThemeSnapshotResult>));
        Assert.Equal(typeof(GetThemeSnapshotQueryHandler), handler.ImplementationType);
    }

    [Fact]
    public void BuilderPageItem_IsTheSiteBuilderType()
    {
        CreateServices();

        Assert.Equal(typeof(SiteBuilderPageItemType), GraphTypeExtensionHelper.GetActualType<BuilderPageItemType>());
    }

    [Fact]
    public void Schema_KeepsTheBuilderPageAndThemeSnapshotContract()
    {
        var services = CreateServices();
        services.AddSingleton(Mock.Of<IGroupedPageService>());
        services.AddSingleton(Mock.Of<IOptionalDependency<PageBuilderGroupedPageService>>());
        using var serviceProvider = services.BuildServiceProvider();

        var schema = new BuilderPageAndThemeSnapshotSchema(serviceProvider);
        schema.Initialize();
        var sdl = schema.Print(new PrintOptions { IncludeDescriptions = false, IncludeDeprecationReasons = false });

        Assert.Equal(
            """
            type Query {
              builderPage(storeId: String!, pageId: String!): BuilderPageItem
              themeSnapshot(domain: String, storeId: String, cultureName: String, permalink: String): ThemeSnapshot
            }

            type BuilderPageItem {
              permalink: String
              content: String
            }

            type ThemeSnapshot {
              storeId: String!
              cultureName: String!
              publication: Int!
              content: String!
              menus: [MenuLinkList!]!
              slug: ThemeSnapshotSlug
            }

            type MenuLinkList {
              name: String!
              outerId: String
              items: [MenuLink!]!
            }

            type MenuLink {
              title: String!
              url: String!
              priority: Int!
              associatedObjectId: String
              associatedObjectName: String
              associatedObjectType: String
              outerId: String
              childItems: [MenuLink!]!
            }

            type ThemeSnapshotSlug {
              objectType: String!
              objectId: String!
              page: ThemeSnapshotSlugPage
            }

            type ThemeSnapshotSlugPage {
              source: String
              permalink: String
              settings: String
            }

            """.ReplaceLineEndings("\n"),
            sdl.ReplaceLineEndings("\n"));
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        _ = new GraphQLBuilder(services, builder =>
        {
            builder.AddSchema(services, typeof(CoreAssemblyMarker), typeof(DataAssemblyMarker));
        });

        _ = new GraphQLBuilder(services, builder =>
        {
            builder.AddSchema(services, typeof(XapiAssemblyMarker));
        });
        services.AddSiteBuilderPreview();

        return services;
    }

    private sealed class BuilderPageAndThemeSnapshotSchema(System.IServiceProvider services)
        : SchemaFactory(services.GetServices<ISchemaBuilder>(), services, new DefaultSchemaFilter())
    {
        protected override System.Collections.Generic.List<ISchemaBuilder> GetSchemaBuilders()
        {
            return base.GetSchemaBuilders().Where(x => x is BuilderPageSchema or ThemeSnapshotSchema).ToList();
        }
    }
}
