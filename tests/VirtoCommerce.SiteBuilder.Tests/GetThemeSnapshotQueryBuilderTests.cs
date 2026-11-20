using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GraphQL;
using GraphQL.Execution;
using GraphQL.Types;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VirtoCommerce.SiteBuilder.Xapi.Models;
using VirtoCommerce.SiteBuilder.Xapi.Queries;
using VirtoCommerce.SiteBuilder.Xapi.Schemas;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class GetThemeSnapshotQueryBuilderTests : IDisposable
{
    private readonly List<ServiceProvider> _serviceProviders = [];

    [Fact]
    public void Build_AddsThemeSnapshot_WithOptionalDomainStoreIdCultureNameAndPermalink()
    {
        var (schema, field) = BuildField();

        Assert.Equal(typeof(ThemeSnapshotType), field.Type);
        Assert.Equal(
            [("domain", typeof(StringGraphType)), ("storeId", typeof(StringGraphType)), ("cultureName", typeof(StringGraphType)), ("permalink", typeof(StringGraphType))],
            field.Arguments.Select(x => (schema.NameConverter.NameForArgument(x.Name, schema.Query, field), x.Type)));
    }

    [Fact]
    public async Task Resolve_SendsTheArgumentsAsTheQuery()
    {
        GetThemeSnapshotQuery query = null;
        var snapshot = new ThemeSnapshotResult();
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        mediator
            .Setup(x => x.Send(It.IsAny<GetThemeSnapshotQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<ThemeSnapshotResult>, CancellationToken>((request, _) => query = (GetThemeSnapshotQuery)request)
            .ReturnsAsync(snapshot);

        var (schema, field) = BuildField();

        var result = await field.Resolver.ResolveAsync(new ResolveFieldContext
        {
            Schema = schema,
            ParentType = schema.Query,
            FieldDefinition = field,
            RequestServices = CreateServices(mediator.Object),
            Arguments = new Dictionary<string, ArgumentValue>
            {
                ["domain"] = new("shop.example.com", ArgumentSource.Literal),
                ["storeId"] = new("B2B-store", ArgumentSource.Literal),
                ["cultureName"] = new("de-DE", ArgumentSource.Literal),
                ["permalink"] = new("printers/laser", ArgumentSource.Literal),
            },
        });

        Assert.Same(snapshot, result);
        Assert.NotNull(query);
        Assert.Equal("shop.example.com", query.Domain);
        Assert.Equal("B2B-store", query.StoreId);
        Assert.Equal("de-DE", query.CultureName);
        Assert.Equal("printers/laser", query.Permalink);
    }

    public void Dispose()
    {
        foreach (var serviceProvider in _serviceProviders)
        {
            serviceProvider.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static (Schema Schema, FieldType Field) BuildField()
    {
        var schema = new Schema { Query = new ObjectGraphType() };
        new GetThemeSnapshotQueryBuilder(new FakeAuthorizationService(allowed: false)).Build(schema);

        return (schema, schema.Query.GetField("themeSnapshot"));
    }

    private ServiceProvider CreateServices(IMediator mediator)
    {
        var services = new ServiceCollection();
        services.AddSingleton(mediator);

        var serviceProvider = services.BuildServiceProvider();
        _serviceProviders.Add(serviceProvider);

        return serviceProvider;
    }
}
