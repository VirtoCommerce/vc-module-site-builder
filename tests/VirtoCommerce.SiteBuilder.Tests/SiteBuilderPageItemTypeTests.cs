using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GraphQL;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VirtoCommerce.Pages.Core.ContentProviders;
using VirtoCommerce.Pages.Core.Models;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Xapi.Schemas;
using VirtoCommerce.XCMS.Core.Models;
using VirtoCommerce.XCMS.Core.Schemas;
using Xunit;
using PageBuilderGroupedPageService = VirtoCommerce.PageBuilderModule.Core.Services.IGroupedPageService;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderPageItemTypeTests : IDisposable
{
    private const string PageId = "draft-page";
    private const string RawContent = "{\"content\":[{\"id\":\"placement\",\"type\":\"componentRef\",\"componentRef\":\"shared\"}]}";
    private const string ResolvedContent = "{\"content\":[{\"id\":\"placement_title\",\"type\":\"text\",\"text\":\"Shared title\"}]}";

    private readonly List<ServiceProvider> _serviceProviders = [];

    [Fact]
    public void Fields_AreTheBuilderPageItemOfXcms()
    {
        var type = new SiteBuilderPageItemType(NoPageBuilder(), GroupedPages().Object);
        var xcmsType = new BuilderPageItemType(NoPageBuilder());

        Assert.Equal(xcmsType.Name, type.Name);
        Assert.Equal("BuilderPageItem", type.Name);
        Assert.Equal(xcmsType.Fields.Select(x => (x.Name, x.Type)), type.Fields.Select(x => (x.Name, x.Type)));
    }

    [Fact]
    public async Task Content_GraphQLFieldUsesTheSiteBuilderContentProvider()
    {
        var type = new SiteBuilderPageItemType(NoPageBuilder(), GroupedPages().Object);
        var field = type.Fields.Single(x => x.Name == nameof(BuilderPageItem.Content));
        var provider = CreateProvider(ModuleConstants.ContentProviders.SiteBuilder, [new PageDocument { Id = PageId, Content = ResolvedContent }]);

        var result = await field.Resolver.ResolveAsync(new ResolveFieldContext
        {
            Source = new BuilderPageItem { PageId = PageId },
            RequestServices = CreateRequestServices(provider.Object),
        });

        Assert.Equal(ResolvedContent, result);
    }

    [Fact]
    public async Task Content_UsesTheProviderDocumentInsteadOfTheRawAuthoringContent()
    {
        var groupedPages = GroupedPages();
        var provider = CreateProvider("PageBuilder", [new PageDocument { Id = PageId, Content = ResolvedContent }]);
        var otherProvider = new Mock<IPageContentProvider>(MockBehavior.Strict);
        otherProvider.SetupGet(x => x.ProviderName).Returns("Other");

        var result = await ResolveContentAsync(groupedPages, new BuilderPageItem { PageId = PageId }, otherProvider.Object, provider.Object);

        Assert.Equal(ResolvedContent, result);
        groupedPages.Verify(x => x.LoadContent(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Content_WhenTheProviderSkipsThePage_LogsAndDoesNotExposeRawReferences()
    {
        var groupedPages = GroupedPages();
        var provider = CreateProvider("PageBuilder", []);
        var logger = new Mock<ILogger<BuilderPageItemType>>();

        var result = await new TestSiteBuilderPageItemType(groupedPages.Object).ResolveContentAsync(new ResolveFieldContext<BuilderPageItem>
        {
            Source = new BuilderPageItem { PageId = PageId },
            RequestServices = CreateRequestServices([provider.Object], logger.Object),
        });

        Assert.Null(result);
        groupedPages.Verify(x => x.LoadContent(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        logger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString().Contains(PageId)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task Content_DoesNotReturnAnotherPageFromTheProvider()
    {
        var provider = CreateProvider("PageBuilder", [new PageDocument { Id = "other-page", Content = "Other content" }]);

        Assert.Null(await ResolveContentAsync(GroupedPages(), new BuilderPageItem { PageId = PageId }, provider.Object));
    }

    [Fact]
    public async Task Content_ThatIsAlreadyThere_IsKept()
    {
        var provider = new Mock<IPageContentProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.ProviderName).Returns("PageBuilder");

        Assert.Equal("Static content", await ResolveContentAsync(GroupedPages(), new BuilderPageItem { PageId = PageId, Content = "Static content" }, provider.Object));
    }

    [Fact]
    public async Task Content_WithoutTheContentProvider_LoadsTheSiteBuilderPage()
    {
        Assert.Equal(RawContent, await ResolveContentAsync(GroupedPages(), new BuilderPageItem { PageId = PageId }));
    }

    [Fact]
    public async Task Content_WithoutRequestScope_LoadsTheSiteBuilderPage()
    {
        var result = await new TestSiteBuilderPageItemType(GroupedPages().Object).ResolveContentAsync(new ResolveFieldContext<BuilderPageItem>
        {
            Source = new BuilderPageItem { PageId = PageId },
        });

        Assert.Equal(RawContent, result);
    }

    [Fact]
    public async Task Content_WithoutPageId_ReadsNothing()
    {
        var provider = new Mock<IPageContentProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.ProviderName).Returns("PageBuilder");

        Assert.Null(await ResolveContentAsync(new Mock<IGroupedPageService>(MockBehavior.Strict), new BuilderPageItem(), provider.Object));
    }

    public void Dispose()
    {
        foreach (var serviceProvider in _serviceProviders)
        {
            serviceProvider.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private Task<string> ResolveContentAsync(Mock<IGroupedPageService> groupedPages, BuilderPageItem source, params IPageContentProvider[] providers)
    {
        return new TestSiteBuilderPageItemType(groupedPages.Object).ResolveContentAsync(new ResolveFieldContext<BuilderPageItem>
        {
            Source = source,
            RequestServices = CreateRequestServices(providers),
        });
    }

    private ServiceProvider CreateRequestServices(params IPageContentProvider[] providers)
    {
        return CreateRequestServices(providers, logger: null);
    }

    private ServiceProvider CreateRequestServices(IPageContentProvider[] providers, ILogger<BuilderPageItemType> logger)
    {
        var services = new ServiceCollection();

        foreach (var provider in providers)
        {
            services.AddSingleton(provider);
        }

        if (logger != null)
        {
            services.AddSingleton(logger);
        }

        var serviceProvider = services.BuildServiceProvider();
        _serviceProviders.Add(serviceProvider);

        return serviceProvider;
    }

    private static Mock<IPageContentProvider> CreateProvider(string name, IList<PageDocument> documents)
    {
        var provider = new Mock<IPageContentProvider>(MockBehavior.Strict);
        provider.SetupGet(x => x.ProviderName).Returns(name);
        provider.Setup(x => x.GetByIdsAsync(It.Is<IList<string>>(ids => ids.SequenceEqual(new[] { PageId }))))
            .ReturnsAsync(documents);
        return provider;
    }

    private static Mock<IGroupedPageService> GroupedPages()
    {
        var service = new Mock<IGroupedPageService>(MockBehavior.Strict);
        service.Setup(x => x.LoadContent(PageId, It.IsAny<CancellationToken>())).ReturnsAsync(RawContent);
        return service;
    }

    private static IOptionalDependency<PageBuilderGroupedPageService> NoPageBuilder()
    {
        var dependency = new Mock<IOptionalDependency<PageBuilderGroupedPageService>>(MockBehavior.Strict);
        dependency.SetupGet(x => x.HasValue).Returns(false);
        return dependency.Object;
    }

    private sealed class TestSiteBuilderPageItemType(IGroupedPageService groupedPages)
        : SiteBuilderPageItemType(NoPageBuilder(), groupedPages)
    {
        public Task<string> ResolveContentAsync(IResolveFieldContext<BuilderPageItem> context)
        {
            return LoadContent(context);
        }
    }
}
