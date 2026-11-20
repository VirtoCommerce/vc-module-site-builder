using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.ContentModule.Core.Model;
using VirtoCommerce.ContentModule.Core.Services;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Xapi.Queries;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Services;
using VirtoCommerce.XCMS.Core.Queries;
using Xunit;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Tests;

public class GetSiteBuilderPreviewPageQueryHandlerTests
{
    private const string StoreId = "B2B-store";

    private readonly List<SiteBuilderPage> _pages = [];
    private readonly List<GroupedSiteBuilderPage> _groups = [];
    private readonly Mock<IContentService> _content = new(MockBehavior.Strict);

    [Fact]
    public async Task Handle_ForAPage_ReturnsItAndThePermalinkOfItsGroup()
    {
        AddGroup("group-1", "/spring", Page("page-1", Published));

        var result = await Handle("page-1");

        Assert.Equal("page-1", result.PageId);
        Assert.Equal("/spring", result.Permalink);
        Assert.Null(result.Content);
    }

    [Theory]
    [InlineData(new[] { Published, Draft, Archived }, "Draft")]
    [InlineData(new[] { Archived, Published }, "Published")]
    [InlineData(new[] { Archived, Archived }, "Archived-1")]
    public async Task Handle_ForAGroup_PreviewsItsDraft_ElseItsPublished_ElseItsLatestArchivedPage(string[] statuses, string expected)
    {
        var pages = statuses
            .Select((status, i) => Page(status == Archived ? $"{status}-{i}" : status, status, new DateTime(2026, 1, 1).AddDays(i)))
            .ToArray();
        AddGroup("group-1", "/spring", pages);
        var query = new GetBuilderPageQuery { StoreId = StoreId, PageId = "group-1" };

        var result = await CreateHandler(pagesEnabled: true).Handle(query, CancellationToken.None);

        Assert.Equal(expected, result.PageId);
        Assert.Equal(expected, query.PageId);
        Assert.Equal("/spring", result.Permalink);
    }

    [Fact]
    public async Task Handle_ForAnUnknownId_ReturnsAnEmptyItem()
    {
        var result = await Handle("unknown");

        Assert.NotNull(result);
        Assert.Null(result.PageId);
        Assert.Null(result.Permalink);
    }

    [Fact]
    public async Task Handle_WithPagesOff_ReturnsTheContentFile()
    {
        _content
            .Setup(x => x.GetFileContentAsync("/spring.page"))
            .ReturnsAsync(new IndexableContentFile { Permalink = "/spring", Content = "{\"content\":[]}" });

        var result = await CreateHandler(pagesEnabled: false).Handle(new GetBuilderPageQuery { StoreId = StoreId, PageId = "/spring.page" }, CancellationToken.None);

        Assert.Equal("/spring.page", result.PageId);
        Assert.Equal("/spring", result.Permalink);
        Assert.Equal("{\"content\":[]}", result.Content);
    }

    private Task<VirtoCommerce.XCMS.Core.Models.BuilderPageItem> Handle(string id)
    {
        return CreateHandler(pagesEnabled: true).Handle(new GetBuilderPageQuery { StoreId = StoreId, PageId = id }, CancellationToken.None);
    }

    private static SiteBuilderPage Page(string id, string status, DateTime? modifiedDate = null) =>
        new() { Id = id, StoreId = StoreId, Status = status, ModifiedDate = modifiedDate };

    private void AddGroup(string id, string permalink, params SiteBuilderPage[] pages)
    {
        foreach (var page in pages)
        {
            page.GroupId = id;
        }

        _pages.AddRange(pages);
        _groups.Add(new GroupedSiteBuilderPage { Id = id, StoreId = StoreId, Permalink = permalink, Pages = pages });
    }

    private GetSiteBuilderPreviewPageQueryHandler CreateHandler(bool pagesEnabled)
    {
        var pageSearch = new Mock<ISiteBuilderPageSearchService>(MockBehavior.Strict);
        pageSearch
            .Setup(x => x.SearchAsync(It.IsAny<SiteBuilderPageSearchCriteria>(), It.IsAny<bool>()))
            .ReturnsAsync((SiteBuilderPageSearchCriteria criteria, bool _) =>
            {
                var results = _pages.Where(x => x.StoreId == criteria.StoreId && criteria.ObjectIds.Contains(x.Id)).ToList();
                return new SiteBuilderPageSearchResult { TotalCount = results.Count, Results = results };
            });

        var groupSearch = new Mock<IGroupedPageSearchService>(MockBehavior.Strict);
        groupSearch
            .Setup(x => x.SearchAsync(It.IsAny<SiteBuilderPageSearchCriteria>(), It.IsAny<bool>()))
            .ReturnsAsync((SiteBuilderPageSearchCriteria criteria, bool _) =>
            {
                var results = _groups.Where(x => x.StoreId == criteria.StoreId && criteria.ObjectIds.Contains(x.Id)).ToList();
                return new GroupedSiteBuilderPageSearchResult { TotalCount = results.Count, Results = results };
            });

        var store = new Store
        {
            Id = StoreId,
            Settings =
            [
                new ObjectSettingEntry(Pages.Core.ModuleConstants.Settings.General.Enable) { Value = pagesEnabled },
            ],
        };
        var stores = new Mock<IStoreService>(MockBehavior.Strict);
        stores
            .Setup(x => x.GetAsync(It.Is<IList<string>>(ids => ids.SequenceEqual(new[] { StoreId })), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync([store]);

        return new GetSiteBuilderPreviewPageQueryHandler(pageSearch.Object, groupSearch.Object, _content.Object, stores.Object);
    }
}
