using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VirtoCommerce.ContentModule.Core.Model;
using VirtoCommerce.ContentModule.Core.Services;
using VirtoCommerce.Pages.Core.Models;
using VirtoCommerce.Seo.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Xapi.Queries;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.Xapi.Core.Models;
using VirtoCommerce.Xapi.Core.Queries;
using VirtoCommerce.Xapi.Core.Services;
using VirtoCommerce.XCMS.Core.Models;
using VirtoCommerce.XCMS.Core.Queries;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class GetThemeSnapshotQueryHandlerTests
{
    private const string StoreId = "B2B-store";

    [Fact]
    public async Task Handle_FindsTheStoreByDomain_AndReturnsItsSnapshot()
    {
        // Arrange
        StoreDomainRequest storeRequest = null;
        var stores = new Mock<IStoreDomainResolverService>();
        stores
            .Setup(x => x.GetStoreAsync(It.IsAny<StoreDomainRequest>()))
            .Callback<StoreDomainRequest>(x => storeRequest = x)
            .ReturnsAsync(new Store { Id = StoreId });
        var snapshots = new Mock<IThemeSnapshotService>();
        snapshots.Setup(x => x.IsNewSiteBuilderEnabledAsync(StoreId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        snapshots
            .Setup(x => x.GetPublishedAsync(StoreId, "de", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ThemeSnapshot(StoreId, "de-DE", 4, "{\"format\":1}", []));

        // Act
        var result = await CreateHandler(stores.Object, snapshots.Object, new Mock<IMenuLinkListSearchService>(MockBehavior.Strict).Object).Handle(
            new GetThemeSnapshotQuery { Domain = "shop.example.com", CultureName = "de" },
            CancellationToken.None);

        // Assert
        Assert.Equal("shop.example.com", storeRequest.Domain);
        Assert.Null(storeRequest.StoreId);
        Assert.Equal(StoreId, result.StoreId);
        Assert.Equal("de-DE", result.CultureName);
        Assert.Equal(4, result.Publication);
        Assert.Equal("{\"format\":1}", result.Content);
        Assert.Empty(result.Menus);
    }

    [Fact]
    public async Task Handle_PassesTheStoreIdToTheStoreLookup()
    {
        // Arrange
        StoreDomainRequest storeRequest = null;
        var stores = new Mock<IStoreDomainResolverService>();
        stores
            .Setup(x => x.GetStoreAsync(It.IsAny<StoreDomainRequest>()))
            .Callback<StoreDomainRequest>(x => storeRequest = x)
            .ReturnsAsync(new Store { Id = StoreId });

        // Act
        await CreateHandler(stores.Object, Snapshots(new ThemeSnapshot(StoreId, "en-US", 1, "{}", [])), Menus())
            .Handle(new GetThemeSnapshotQuery { StoreId = StoreId, Domain = "shop.example.com" }, CancellationToken.None);

        // Assert
        Assert.Equal(StoreId, storeRequest.StoreId);
        Assert.Equal("shop.example.com", storeRequest.Domain);
    }

    [Fact]
    public async Task Handle_ReturnsTheStoreMenusTheSnapshotNames_InTheServedLanguage_UnderTheNamesItHolds()
    {
        // Arrange
        var menus = Menus(
            List("Other-store", "main-menu", "de-DE", "Fremd"),
            List(StoreId, "Main-Menu", "de-DE", "Shop"),
            List(StoreId, "main-menu", "en-US", "Store"),
            List(StoreId, "Shop", "de-DE", "Neu"));

        // Act
        var result = await CreateHandler(Store(), Snapshots(new ThemeSnapshot(StoreId, "de-DE", 1, "{}", ["main-menu", "gone"])), menus)
            .Handle(new GetThemeSnapshotQuery { Domain = "shop.example.com" }, CancellationToken.None);

        // Assert
        var menu = Assert.Single(result.Menus);
        Assert.Equal("main-menu", menu.Name);
        Assert.Equal(["Shop"], menu.Items.Select(x => x.Link.Title));
        Assert.Equal(["Neu"], menu.Items[0].ChildItems.Select(x => x.Link.Title));
    }

    [Fact]
    public async Task Handle_WithoutTheStore_ReturnsNull()
    {
        // Arrange
        var stores = new Mock<IStoreDomainResolverService>();
        stores.Setup(x => x.GetStoreAsync(It.IsAny<StoreDomainRequest>())).ReturnsAsync((Store)null);

        // Act
        var result = await CreateHandler(
                stores.Object,
                new Mock<IThemeSnapshotService>(MockBehavior.Strict).Object,
                new Mock<IMenuLinkListSearchService>(MockBehavior.Strict).Object)
            .Handle(new GetThemeSnapshotQuery { Domain = "unknown.example.com" }, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task Handle_WithTheNewSiteBuilderOff_ReturnsNull_WithoutReadingTheSnapshotOrTheSlug()
    {
        // Arrange: strict mocks answer the switch only
        var snapshots = new Mock<IThemeSnapshotService>(MockBehavior.Strict);
        snapshots.Setup(x => x.IsNewSiteBuilderEnabledAsync(StoreId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        // Act
        var result = await CreateHandler(
                Store(),
                snapshots.Object,
                new Mock<IMenuLinkListSearchService>(MockBehavior.Strict).Object,
                new Mock<IMediator>(MockBehavior.Strict).Object)
            .Handle(new GetThemeSnapshotQuery { Domain = "shop.example.com", Permalink = "spring" }, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    private const string Rules = "{\"version\":2,\"page\":[{\"id\":\"landing\",\"template\":\"page.landing\",\"when\":{\"setting\":{\"name\":\"campaign\",\"equals\":\"spring\"}}},{\"id\":\"docs\",\"template\":\"page.docs\",\"when\":{\"path\":\"docs/*\"}}]}";

    private static readonly string SnapshotContent = $"{{\"format\":1,\"templates\":{{\"page\":{{\"settings\":{{\"layout\":\"minimal\"}},\"content\":[]}}}},\"rules\":{Rules}}}";

    private const string PageContent = "{\"settings\":{\"name\":\"Spring\",\"header\":\"A long header\",\"layout\":\"blank\",\"template\":\"page.landing\",\"campaign\":\"spring\"},\"content\":[{\"id\":\"1\",\"type\":\"text\"}]}";

    [Fact]
    public async Task Handle_WithoutAPermalink_LooksNothingUp()
    {
        // Act
        var result = await CreateHandler(Store(), Snapshots(new ThemeSnapshot(StoreId, "en-US", 1, SnapshotContent, [])), Menus())
            .Handle(new GetThemeSnapshotQuery { Domain = "shop.example.com" }, CancellationToken.None);

        // Assert
        Assert.Null(result.Slug);
    }

    [Fact]
    public async Task Handle_WithAPermalinkWithNothingAtIt_ReturnsTheSnapshotWithoutASlug()
    {
        // Arrange
        var mediator = Mediator(new SlugInfoResponse { RedirectUrl = "/elsewhere" });

        // Act
        var result = await CreateHandler(Store(), Snapshots(new ThemeSnapshot(StoreId, "en-US", 1, SnapshotContent, [])), Menus(), mediator.Object)
            .Handle(new GetThemeSnapshotQuery { Domain = "shop.example.com", Permalink = "gone" }, CancellationToken.None);

        // Assert
        Assert.Equal(SnapshotContent, result.Content);
        Assert.Null(result.Slug);
    }

    [Fact]
    public async Task Handle_LooksThePermalinkUpWithTheSlugInfoQuery_InTheStoreAndLanguageServed()
    {
        // Arrange
        SlugInfoQuery slugQuery = null;
        var mediator = Mediator(Entity("CatalogProduct", "product-1"));
        mediator
            .Setup(x => x.Send(It.IsAny<SlugInfoQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<SlugInfoResponse>, CancellationToken>((request, _) => slugQuery = (SlugInfoQuery)request)
            .ReturnsAsync(Entity("CatalogProduct", "product-1"));

        // Act
        await CreateHandler(Store(), Snapshots(new ThemeSnapshot(StoreId, "de-DE", 1, SnapshotContent, [])), Menus(), mediator.Object)
            .Handle(new GetThemeSnapshotQuery { Domain = "shop.example.com", CultureName = "de", Permalink = "printers/laser" }, CancellationToken.None);

        // Assert
        Assert.Equal("printers/laser", slugQuery.Permalink);
        Assert.Equal(StoreId, slugQuery.StoreId);
        Assert.Equal("de-DE", slugQuery.CultureName);
        Assert.Null(slugQuery.UserId);
    }

    [Fact]
    public async Task Handle_ForAProduct_ReturnsItsTypeAndReadsNoPage()
    {
        // Arrange: a strict mediator answers the slug query only
        var mediator = Mediator(Entity("CatalogProduct", "product-1"));

        // Act
        var result = await CreateHandler(Store(), Snapshots(new ThemeSnapshot(StoreId, "en-US", 1, SnapshotContent, [])), Menus(), mediator.Object)
            .Handle(new GetThemeSnapshotQuery { Domain = "shop.example.com", Permalink = "printers/laser" }, CancellationToken.None);

        // Assert
        Assert.Equal("CatalogProduct", result.Slug.ObjectType);
        Assert.Equal("product-1", result.Slug.ObjectId);
        Assert.Null(result.Slug.Page);
    }

    [Fact]
    public async Task Handle_ForAPageFromTheDatabase_ReturnsItsSourcePermalinkAndTheSettingsThatChooseItsTemplate()
    {
        // Arrange
        GetSinglePageDocumentQuery documentQuery = null;
        var mediator = Mediator(Entity("Pages", "page-1"));
        mediator
            .Setup(x => x.Send(It.IsAny<GetSinglePageDocumentQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<PageDocument>, CancellationToken>((request, _) => documentQuery = (GetSinglePageDocumentQuery)request)
            .ReturnsAsync(new PageDocument { Id = "page-1", Source = "page-builder", Permalink = "/spring", Content = PageContent });

        // Act
        var result = await CreateHandler(Store(), Snapshots(new ThemeSnapshot(StoreId, "en-US", 1, SnapshotContent, [])), Menus(), mediator.Object)
            .Handle(new GetThemeSnapshotQuery { Domain = "shop.example.com", Permalink = "spring" }, CancellationToken.None);

        // Assert
        Assert.Equal("page-1", documentQuery.Id);
        Assert.Null(documentQuery.UserId);
        Assert.Equal("Pages", result.Slug.ObjectType);
        Assert.Equal("page-builder", result.Slug.Page.Source);
        Assert.Equal("/spring", result.Slug.Page.Permalink);
        Assert.Equal("{\"layout\":\"blank\",\"template\":\"page.landing\",\"campaign\":\"spring\"}", result.Slug.Page.Settings);
    }

    [Fact]
    public async Task Handle_ForAPage_WithoutPageRules_ReturnsItsLayoutAndTemplateOnly()
    {
        // Arrange
        var mediator = Mediator(Entity("Pages", "page-1"));
        mediator
            .Setup(x => x.Send(It.IsAny<GetSinglePageDocumentQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PageDocument { Id = "page-1", Source = "page-builder", Permalink = "/spring", Content = "{\"settings\":{\"campaign\":\"spring\",\"layout\":\"minimal\"},\"content\":[]}" });

        // Act
        var result = await CreateHandler(Store(), Snapshots(new ThemeSnapshot(StoreId, "en-US", 1, "{\"format\":1,\"templates\":{}}", [])), Menus(), mediator.Object)
            .Handle(new GetThemeSnapshotQuery { Domain = "shop.example.com", Permalink = "spring" }, CancellationToken.None);

        // Assert
        Assert.Equal("{\"layout\":\"minimal\"}", result.Slug.Page.Settings);
    }

    [Fact]
    public async Task Handle_ForAPageOfAnotherSource_ReturnsItsSourceWithoutSettings()
    {
        // Arrange
        var mediator = Mediator(Entity("Pages", "page-1"));
        mediator
            .Setup(x => x.Send(It.IsAny<GetSinglePageDocumentQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PageDocument { Id = "page-1", Source = "builder.io", Permalink = "/spring", Content = "[{\"data\":{}}]" });

        // Act
        var result = await CreateHandler(Store(), Snapshots(new ThemeSnapshot(StoreId, "en-US", 1, SnapshotContent, [])), Menus(), mediator.Object)
            .Handle(new GetThemeSnapshotQuery { Domain = "shop.example.com", Permalink = "spring" }, CancellationToken.None);

        // Assert
        Assert.Equal("builder.io", result.Slug.Page.Source);
        Assert.Null(result.Slug.Page.Settings);
    }

    [Fact]
    public async Task Handle_ForAPageTheAnonymousVisitorCannotRead_ReturnsTheSlugWithoutThePage()
    {
        // Arrange
        var mediator = Mediator(Entity("Pages", "page-1"));
        mediator
            .Setup(x => x.Send(It.IsAny<GetSinglePageDocumentQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PageDocument)null);

        // Act
        var result = await CreateHandler(Store(), Snapshots(new ThemeSnapshot(StoreId, "en-US", 1, SnapshotContent, [])), Menus(), mediator.Object)
            .Handle(new GetThemeSnapshotQuery { Domain = "shop.example.com", Permalink = "spring" }, CancellationToken.None);

        // Assert
        Assert.Equal("Pages", result.Slug.ObjectType);
        Assert.Null(result.Slug.Page);
    }

    [Theory]
    [InlineData(PageContent, "{\"layout\":\"blank\",\"template\":\"page.landing\",\"campaign\":\"spring\"}")]
    [InlineData("---\ntitle: Spring\n---\n# Spring", null)]
    public async Task Handle_ForAFilePage_ReadsItsFileAsThePageQueryDoes(string content, string settings)
    {
        // Arrange
        GetSinglePageQuery pageQuery = null;
        var mediator = Mediator(Entity("ContentFile", "file-1"));
        mediator
            .Setup(x => x.Send(It.IsAny<GetSinglePageQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<PageItem>, CancellationToken>((request, _) => pageQuery = (GetSinglePageQuery)request)
            .ReturnsAsync(new PageItem { Id = "file-1", Permalink = "/spring", RelativeUrl = "/spring.en-US.page" });
        var files = new Mock<IContentService>(MockBehavior.Strict);
        files
            .Setup(x => x.GetItemStreamAsync("pages", StoreId, "/spring.en-US.page"))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes(content)));

        // Act
        var result = await CreateHandler(Store(), Snapshots(new ThemeSnapshot(StoreId, "en-US", 1, SnapshotContent, [])), Menus(), mediator.Object, files.Object)
            .Handle(new GetThemeSnapshotQuery { Domain = "shop.example.com", Permalink = "spring" }, CancellationToken.None);

        // Assert
        Assert.Equal("file-1", pageQuery.Id);
        Assert.Equal(StoreId, pageQuery.StoreId);
        Assert.Equal("ContentFile", result.Slug.ObjectType);
        Assert.Null(result.Slug.Page.Source);
        Assert.Equal("/spring", result.Slug.Page.Permalink);
        Assert.Equal(settings, result.Slug.Page.Settings);
    }

    [Fact]
    public async Task Handle_WithoutASnapshot_ReturnsNull_AndReadsNoPage()
    {
        // Arrange: a strict mediator answers the slug query only
        var mediator = Mediator(Entity("Pages", "page-1"));

        // Act
        var result = await CreateHandler(Store(), Snapshots(null), new Mock<IMenuLinkListSearchService>(MockBehavior.Strict).Object, mediator.Object)
            .Handle(new GetThemeSnapshotQuery { Domain = "shop.example.com", Permalink = "spring" }, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task Handle_WhenTheLookupFails_ReturnsTheSnapshotWithoutASlug()
    {
        // Arrange
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        mediator
            .Setup(x => x.Send(It.IsAny<SlugInfoQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The index is down"));

        // Act
        var result = await CreateHandler(Store(), Snapshots(new ThemeSnapshot(StoreId, "en-US", 1, SnapshotContent, [])), Menus(), mediator.Object)
            .Handle(new GetThemeSnapshotQuery { Domain = "shop.example.com", Permalink = "spring" }, CancellationToken.None);

        // Assert
        Assert.Equal(SnapshotContent, result.Content);
        Assert.Null(result.Slug);
    }

    [Fact]
    public async Task Handle_WhenTheSnapshotReadFails_FailsWithoutLookingThePermalinkUp()
    {
        // Arrange
        var mediator = new Mock<IMediator>();
        var snapshots = new Mock<IThemeSnapshotService>();
        snapshots.Setup(x => x.IsNewSiteBuilderEnabledAsync(StoreId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        snapshots
            .Setup(x => x.GetPublishedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("The storage is down"));

        // Act
        var handle = CreateHandler(Store(), snapshots.Object, Menus(), mediator.Object)
            .Handle(new GetThemeSnapshotQuery { Domain = "shop.example.com", Permalink = "spring" }, CancellationToken.None);

        // Assert
        await Assert.ThrowsAsync<IOException>(() => handle);
        mediator.Verify(x => x.Send(It.IsAny<SlugInfoQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static SlugInfoResponse Entity(string objectType, string objectId) => new()
    {
        EntityInfo = new SeoInfo { ObjectType = objectType, ObjectId = objectId },
    };

    private static Mock<IMediator> Mediator(SlugInfoResponse slugInfo)
    {
        var mediator = new Mock<IMediator>(MockBehavior.Strict);
        mediator.Setup(x => x.Send(It.IsAny<SlugInfoQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(slugInfo);
        return mediator;
    }

    private static IStoreDomainResolverService Store()
    {
        var stores = new Mock<IStoreDomainResolverService>();
        stores.Setup(x => x.GetStoreAsync(It.IsAny<StoreDomainRequest>())).ReturnsAsync(new Store { Id = StoreId });
        return stores.Object;
    }

    private static IThemeSnapshotService Snapshots(ThemeSnapshot snapshot)
    {
        var snapshots = new Mock<IThemeSnapshotService>();
        snapshots.Setup(x => x.IsNewSiteBuilderEnabledAsync(StoreId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        snapshots
            .Setup(x => x.GetPublishedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        return snapshots.Object;
    }

    private static MenuLinkList List(string storeId, string name, string language, params string[] titles) => new()
    {
        Name = name,
        Language = language,
        StoreId = storeId,
        MenuLinks = titles.Select(x => new MenuLink { Title = x, Url = $"/{x}" }).ToList(),
    };

    private static IMenuLinkListSearchService Menus(params MenuLinkList[] lists)
    {
        var search = new Mock<IMenuLinkListSearchService>();
        search
            .Setup(x => x.SearchAsync(It.IsAny<MenuLinkListSearchCriteria>(), It.IsAny<bool>()))
            .ReturnsAsync((MenuLinkListSearchCriteria criteria, bool _) =>
            {
                var results = lists.Where(x => x.StoreId == criteria.StoreId).ToList();
                return new MenuLinkListSearchResult { TotalCount = results.Count, Results = results };
            });
        return search.Object;
    }

    private static GetThemeSnapshotQueryHandler CreateHandler(
        IStoreDomainResolverService stores,
        IThemeSnapshotService snapshots,
        IMenuLinkListSearchService menus,
        IMediator mediator = null,
        IContentService content = null)
    {
        return new GetThemeSnapshotQueryHandler(
            stores,
            menus,
            snapshots,
            mediator ?? new Mock<IMediator>(MockBehavior.Strict).Object,
            content ?? new Mock<IContentService>(MockBehavior.Strict).Object,
            NullLogger<GetThemeSnapshotQueryHandler>.Instance);
    }
}
