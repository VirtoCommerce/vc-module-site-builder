using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Web.Controllers.Api;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Services;
using Xunit;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderPermalinkConflictTests
{
    [Fact]
    public void GetConflict_SameAudience_IsCertain()
    {
        Assert.Equal(SiteBuilderPermalinkConflict.Certain, SiteBuilderPermalinkConflicts.GetConflict(Group(), PublishedGroup()));
    }

    [Theory]
    [InlineData("/about", "about")]
    [InlineData("/About", "/about")]
    [InlineData(" /about", "/about")]
    public void GetConflict_PermalinkDiffersOnlyInCaseOrSlash_IsCertain(string permalink, string otherPermalink)
    {
        var page = Group(x => x.Permalink = permalink);
        var other = PublishedGroup(x => x.Permalink = otherPermalink);

        Assert.Equal(SiteBuilderPermalinkConflict.Certain, SiteBuilderPermalinkConflicts.GetConflict(page, other));
    }

    [Fact]
    public void GetConflict_OtherPageIsOver_IsNoneOnlyAfterItsEnd()
    {
        var other = PublishedGroup(x =>
        {
            x.StartDate = Date(1);
            x.EndDate = Date(31);
        });

        Assert.Equal(SiteBuilderPermalinkConflict.Certain, SiteBuilderPermalinkConflicts.GetConflict(Group(), other, Date(15)));
        Assert.Equal(SiteBuilderPermalinkConflict.None, SiteBuilderPermalinkConflicts.GetConflict(Group(), other, Date(31).AddDays(1)));
    }

    [Fact]
    public void GetConflict_OtherIsDraft_IsNone()
    {
        var other = Group(x => x.Pages = [new SiteBuilderPage { Id = "draft", Status = Draft }]);

        Assert.Equal(SiteBuilderPermalinkConflict.None, SiteBuilderPermalinkConflicts.GetConflict(Group(), other));
    }

    [Fact]
    public void GetConflict_OtherHasPublishedPageAndDraft_IsCertain()
    {
        var other = Group(x => x.Pages =
        [
            new SiteBuilderPage { Id = "published", Status = Published },
            new SiteBuilderPage { Id = "draft", Status = Draft },
        ]);

        Assert.Equal(SiteBuilderPermalinkConflict.Certain, SiteBuilderPermalinkConflicts.GetConflict(Group(), other));
    }

    public static TheoryData<string, Action<GroupedSiteBuilderPage>> NotConflicting => new()
    {
        { "other permalink", x => x.Permalink = "/contacts" },
        { "other store", x => x.StoreId = "other-store" },
        { "other language", x => x.CultureName = "de-DE" },
        { "other organization", x => x.OrganizationId = "org-b" },
        // The storefront prefers the more specific page, so a general page next to it is intended.
        { "any language", x => x.CultureName = null },
        { "any organization", x => x.OrganizationId = null },
        { "other visibility", x => x.Visibility = true },
        { "for user groups", x => x.UserGroups = "vip" },
        { "ends before", x => { x.StartDate = Date(1); x.EndDate = Date(9); } },
        { "starts after", x => x.StartDate = Date(21) },
    };

    [Theory]
    [MemberData(nameof(NotConflicting), DisableDiscoveryEnumeration = true)]
    public void GetConflict_NotOfferedTogetherOrNotEquallySpecific_IsNone(string difference, Action<GroupedSiteBuilderPage> changeOther)
    {
        var page = Group(x =>
        {
            x.OrganizationId = "org-a";
            x.StartDate = Date(10);
            x.EndDate = Date(20);
        });
        var other = PublishedGroup(x =>
        {
            x.OrganizationId = "org-a";
            changeOther(x);
        });

        Assert.True(SiteBuilderPermalinkConflicts.GetConflict(page, other, Now) == SiteBuilderPermalinkConflict.None, difference);
    }

    // The page is shown for the whole rest of the other page's period.
    public static TheoryData<string, Action<GroupedSiteBuilderPage>> Covered => new()
    {
        { "same page settings", _ => { } },
        { "language in another case", x => x.CultureName = "EN-us" },
        { "within the page's dates", x => { x.StartDate = Date(12); x.EndDate = Date(18); } },
    };

    [Theory]
    [MemberData(nameof(Covered), DisableDiscoveryEnumeration = true)]
    public void GetConflict_EquallySpecificAndCovered_IsCertain(string difference, Action<GroupedSiteBuilderPage> changeOther)
    {
        var page = Group(x =>
        {
            x.OrganizationId = "org-a";
            x.StartDate = Date(10);
            x.EndDate = Date(20);
        });
        var other = PublishedGroup(x =>
        {
            x.OrganizationId = "org-a";
            x.StartDate = Date(10);
            x.EndDate = Date(20);
            changeOther(x);
        });

        Assert.True(SiteBuilderPermalinkConflicts.GetConflict(page, other, Now) == SiteBuilderPermalinkConflict.Certain, difference);
    }

    // The other page is still shown on dates the page is not, so unpublishing it would leave those dates empty.
    public static TheoryData<string, Action<GroupedSiteBuilderPage>> PartlyCovered => new()
    {
        { "no dates", x => { x.StartDate = null; x.EndDate = null; } },
        { "dates overlap", x => { x.StartDate = Date(15); x.EndDate = Date(30); } },
        { "ends on the start day", x => { x.StartDate = Date(1); x.EndDate = Date(10); } },
    };

    [Theory]
    [MemberData(nameof(PartlyCovered), DisableDiscoveryEnumeration = true)]
    public void GetConflict_EquallySpecificAndPartlyCovered_IsPossible(string difference, Action<GroupedSiteBuilderPage> changeOther)
    {
        var page = Group(x =>
        {
            x.OrganizationId = "org-a";
            x.StartDate = Date(10);
            x.EndDate = Date(20);
        });
        var other = PublishedGroup(x =>
        {
            x.OrganizationId = "org-a";
            changeOther(x);
        });

        Assert.True(SiteBuilderPermalinkConflicts.GetConflict(page, other, Now) == SiteBuilderPermalinkConflict.Possible, difference);
    }

    [Theory]
    // The storefront prefers the store's default language to any language for every visitor.
    [InlineData("en-US", null, SiteBuilderPermalinkConflict.Certain)]
    [InlineData(null, "en-US", SiteBuilderPermalinkConflict.Certain)]
    // Visitors in other languages still get the page for any language.
    [InlineData("de-DE", null, SiteBuilderPermalinkConflict.None)]
    [InlineData(null, "de-DE", SiteBuilderPermalinkConflict.None)]
    public void GetConflict_AnyLanguageAndStoreDefaultLanguage(string cultureName, string otherCultureName, SiteBuilderPermalinkConflict expected)
    {
        var page = Group(x => x.CultureName = cultureName);
        var other = PublishedGroup(x => x.CultureName = otherCultureName);

        Assert.Equal(expected, SiteBuilderPermalinkConflicts.GetConflict(page, other, Now, storeDefaultLanguage: "en-US"));
    }

    [Theory]
    // The page reaches every visitor the other page is for. Pages for user groups go to signed-in members only,
    // so their visibility does not matter.
    [InlineData(true, "", true, "", SiteBuilderPermalinkConflict.Certain)]
    [InlineData(false, "", false, "", SiteBuilderPermalinkConflict.Certain)]
    [InlineData(false, "vip, gold", false, "Gold", SiteBuilderPermalinkConflict.Certain)]
    [InlineData(true, "vip", false, "vip", SiteBuilderPermalinkConflict.Certain)]
    // The other page keeps members of its own: only a visitor in both pages' groups gets both.
    [InlineData(false, "gold", false, "vip, gold", SiteBuilderPermalinkConflict.Possible)]
    [InlineData(false, "vip", false, "gold", SiteBuilderPermalinkConflict.Possible)]
    [InlineData(true, "vip", true, "gold", SiteBuilderPermalinkConflict.Possible)]
    // The more specific page wins for its audience: a page for user groups over one for everyone, a page for
    // registered users over a public one.
    [InlineData(false, "", false, "gold", SiteBuilderPermalinkConflict.None)]
    [InlineData(true, "", true, "gold", SiteBuilderPermalinkConflict.None)]
    [InlineData(true, "", false, "", SiteBuilderPermalinkConflict.None)]
    public void GetConflict_Audience(bool visibility, string userGroups, bool otherVisibility, string otherUserGroups, SiteBuilderPermalinkConflict expected)
    {
        var page = Group(x =>
        {
            x.Visibility = visibility;
            x.UserGroups = userGroups;
        });
        var other = PublishedGroup(x =>
        {
            x.Visibility = otherVisibility;
            x.UserGroups = otherUserGroups;
        });

        Assert.Equal(expected, SiteBuilderPermalinkConflicts.GetConflict(page, other));
    }

    [Fact]
    public void ApplyPermalinkFilter_MatchesIgnoringCaseAndLeadingSlash()
    {
        var entities = new List<GroupedSiteBuilderPageEntity>
        {
            new() { Id = "slash", Permalink = "/about" },
            new() { Id = "no-slash", Permalink = "about" },
            new() { Id = "case", Permalink = "/About" },
            new() { Id = "other", Permalink = "/about-us" },
            new() { Id = "empty", Permalink = null },
        };

        var result = GroupedPageSearchService.ApplyPermalinkFilter(entities.AsQueryable(), "About").Select(x => x.Id);

        Assert.Equal(["slash", "no-slash", "case"], result);
    }

    [Fact]
    public async Task FindPermalinkUsages_ReturnsOtherNotArchivedPagesWithConflicts()
    {
        var service = CreateService(
            Group(x => x.Id = "self"),
            PublishedGroup(x => x.Id = "published"),
            Group(x => x.Id = "draft"),
            Group(x =>
            {
                x.Id = "archived";
                x.Pages = [new SiteBuilderPage { Id = "archived-page", Status = Archived }];
            }),
            PublishedGroup(x =>
            {
                x.Id = "other-permalink";
                x.Permalink = "/contacts";
            }));

        var usages = await new SeededGroupedPageSearchService(service).FindPermalinkUsagesAsync(Group(x => x.Id = "self"));

        Assert.Equal(["published", "draft"], usages.Select(x => x.GroupId));
        Assert.Equal(SiteBuilderPermalinkConflict.Certain, usages[0].Conflict);
        Assert.Equal(SiteBuilderPermalinkConflict.None, usages[1].Conflict);
    }

    [Fact]
    public async Task PublishGroup_CertainConflict_ReturnsConflictAndChangesNothing()
    {
        var service = CreateService(Group(x => x.Id = "new"), PublishedGroup(x => x.Id = "old"));
        var controller = CreateController(service);

        var result = await controller.PublishGroup("new", publish: true, cancellationToken: TestContext.Current.CancellationToken);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal("old", Assert.Single(Assert.IsType<SiteBuilderPermalinkConflictResult>(conflict.Value).Conflicts).GroupId);
        Assert.Equal(Draft, (await service.GetByIdAsync("new")).Status);
        Assert.Equal(Published, (await service.GetByIdAsync("old")).Status);
    }

    [Fact]
    public async Task PublishGroup_UnpublishConflicting_PublishesAndUnpublishesTheOtherPage()
    {
        var service = CreateService(Group(x => x.Id = "new"), PublishedGroup(x => x.Id = "old"));
        var controller = CreateController(service);

        var result = await controller.PublishGroup("new", publish: true, unpublishConflicting: true, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
        Assert.Equal(Published, (await service.GetByIdAsync("new")).Status);
        var old = await service.GetByIdAsync("old");
        Assert.Equal(Draft, old.Status);
        Assert.Single(old.Pages);
    }

    [Fact]
    public async Task PublishGroup_UnpublishConflictingWithDraft_ReturnsConflictAndChangesNothing()
    {
        var service = CreateService(
            Group(x => x.Id = "new"),
            Group(x =>
            {
                x.Id = "old";
                x.Pages =
                [
                    new SiteBuilderPage { Id = "old-published", Status = Published },
                    new SiteBuilderPage { Id = "old-draft", Status = Draft },
                ];
            }));
        var controller = CreateController(service);

        var result = await controller.PublishGroup("new", publish: true, unpublishConflicting: true, TestContext.Current.CancellationToken);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(Draft, (await service.GetByIdAsync("new")).Status);
        Assert.Contains((await service.GetByIdAsync("old")).Pages, x => x.Status == Published);
    }

    [Fact]
    public async Task PublishGroup_OtherPageChangedAfterTheSearch_IsNotUnpublished()
    {
        var service = CreateService(
            Group(x => x.Id = "new"),
            PublishedGroup(x =>
            {
                x.Id = "old";
                x.Permalink = "/about-us";
            }));
        var staleSearchResult = PublishedGroup(x => x.Id = "old");
        var controller = CreateController(service, new FixedGroupedPageSearchService([staleSearchResult]));

        var result = await controller.PublishGroup("new", publish: true, unpublishConflicting: true, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
        Assert.Equal(Published, (await service.GetByIdAsync("new")).Status);
        Assert.Equal(Published, (await service.GetByIdAsync("old")).Status);
    }

    [Fact]
    public async Task PublishGroup_OtherPageGotDraftAfterTheSearch_ReturnsConflictAndChangesNothing()
    {
        var service = CreateService(
            Group(x => x.Id = "new"),
            Group(x =>
            {
                x.Id = "old";
                x.Pages =
                [
                    new SiteBuilderPage { Id = "old-published", Status = Published },
                    new SiteBuilderPage { Id = "old-draft", Status = Draft },
                ];
            }));
        var staleSearchResult = PublishedGroup(x => x.Id = "old");
        var controller = CreateController(service, new FixedGroupedPageSearchService([staleSearchResult]));

        var result = await controller.PublishGroup("new", publish: true, unpublishConflicting: true, TestContext.Current.CancellationToken);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(Draft, (await service.GetByIdAsync("new")).Status);
        Assert.Contains((await service.GetByIdAsync("old")).Pages, x => x.Status == Published);
    }

    [Fact]
    public async Task PublishGroup_OtherPageUnpublishedAfterTheSearch_PublishesAndLeavesItAlone()
    {
        var service = CreateService(Group(x => x.Id = "new"), Group(x => x.Id = "old"));
        var staleSearchResult = PublishedGroup(x => x.Id = "old");
        var controller = CreateController(service, new FixedGroupedPageSearchService([staleSearchResult]));

        var result = await controller.PublishGroup("new", publish: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
        Assert.Equal(Published, (await service.GetByIdAsync("new")).Status);
        Assert.Equal(Draft, (await service.GetByIdAsync("old")).Status);
    }

    [Fact]
    public async Task PublishGroup_PageForAnyLanguagePublishedInStoreDefaultLanguage_ReturnsConflict()
    {
        var service = CreateService(
            Group(x => x.Id = "new"),
            PublishedGroup(x =>
            {
                x.Id = "old";
                x.CultureName = null;
            }));
        var store = new Store { Id = StoreId, DefaultLanguage = "en-US" };
        var controller = CreateController(service, storeService: new FakeStoreService(store));

        var result = await controller.PublishGroup("new", publish: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(Published, (await service.GetByIdAsync("old")).Status);
    }

    [Fact]
    public async Task PublishGroup_PossibleConflict_Publishes()
    {
        var service = CreateService(
            Group(x =>
            {
                x.Id = "new";
                x.UserGroups = "vip";
            }),
            PublishedGroup(x =>
            {
                x.Id = "old";
                x.UserGroups = "gold";
            }));
        var controller = CreateController(service);

        var result = await controller.PublishGroup("new", publish: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
        Assert.Equal(Published, (await service.GetByIdAsync("new")).Status);
        Assert.Equal(Published, (await service.GetByIdAsync("old")).Status);
    }

    [Fact]
    public async Task PublishGroup_TwoConflictingPagesAtOnce_PublishesOnlyOne()
    {
        var service = CreateService(Group(x => x.Id = "new"), Group(x => x.Id = "old"));
        var searchService = new BarrierGroupedPageSearchService(service, parties: 2);

        var results = await Task.WhenAll(
            CreateController(service, searchService).PublishGroup("new", publish: true, cancellationToken: TestContext.Current.CancellationToken),
            CreateController(service, searchService).PublishGroup("old", publish: true, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Single(results.OfType<OkResult>());
        Assert.Single(results.OfType<ConflictObjectResult>());
        Assert.Single(await service.GetAsync(["new", "old"]), x => x.Status == Published);
    }

    [Fact]
    public async Task PublishAsync_DryRunWithCertainConflict_IsRefusedAsThePublishIs()
    {
        var service = CreateService(Group(x => x.Id = "new"), PublishedGroup(x => x.Id = "old"));
        var request = new GroupedPagePublishOperationRequest { GroupId = "new", Publish = true, DryRun = true };

        var result = await CreateOperations(service).PublishAsync(request, User, TestContext.Current.CancellationToken);

        Assert.Equal(SiteBuilderOperationStatus.PermalinkConflict, result.Status);
        Assert.Equal(Draft, (await service.GetByIdAsync("new")).Status);
        Assert.Equal(Published, (await service.GetByIdAsync("old")).Status);
    }

    [Fact]
    public async Task PublishAsync_DryRunUnpublishingConflicting_PreviewsTheOtherPageUnpublished()
    {
        var service = CreateService(Group(x => x.Id = "new"), PublishedGroup(x => x.Id = "old"));
        var request = new GroupedPagePublishOperationRequest { GroupId = "new", Publish = true, UnpublishConflicting = true, DryRun = true };

        var result = await CreateOperations(service).PublishAsync(request, User, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(
            [("new", Draft, Published), ("old", Published, Draft)],
            result.Preview.Objects.Select(x => (x.ObjectId, x.Fields.Single().From, x.Fields.Single().To)));
        Assert.Equal(Published, (await service.GetByIdAsync("old")).Status);
    }

    [Fact]
    public async Task PublishAsync_ReviewedDraftUnpublishingConflicting_PublishesAndUnpublishesTheOtherPage()
    {
        var service = CreateService(Group(x => x.Id = "new"), PublishedGroup(x => x.Id = "old"));
        service.SeedContent("new-draft", DraftContent);
        var request = new GroupedPagePublishOperationRequest
        {
            GroupId = "new", Publish = true, UnpublishConflicting = true, ExpectedDraftVersion = await DraftVersionAsync(service, "new"),
        };

        var result = await CreateOperations(service).PublishAsync(request, User, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(Published, (await service.GetByIdAsync("new")).Status);
        var old = await service.GetByIdAsync("old");
        Assert.Equal(Draft, old.Status);
        Assert.Single(old.Pages);
    }

    [Fact]
    public async Task PublishAsync_DraftChangedSinceItWasReviewed_LeavesTheConflictingPagePublished()
    {
        var service = CreateService(Group(x => x.Id = "new"), PublishedGroup(x => x.Id = "old"));
        service.SeedContent("new-draft", DraftContent);
        var reviewed = await DraftVersionAsync(service, "new");
        service.SeedContent("new-draft", DraftContent.Replace("text", "image"));
        var request = new GroupedPagePublishOperationRequest { GroupId = "new", Publish = true, UnpublishConflicting = true, ExpectedDraftVersion = reviewed };

        var result = await CreateOperations(service).PublishAsync(request, User, TestContext.Current.CancellationToken);

        Assert.Equal(SiteBuilderOperationStatus.Conflict, result.Status);
        Assert.Equal(Draft, (await service.GetByIdAsync("new")).Status);
        Assert.Equal(Published, (await service.GetByIdAsync("old")).Status);
    }

    [Fact]
    public async Task PublishAsync_AnotherPagePublishedAtThePermalinkSinceTheReview_UnpublishesNothing()
    {
        var service = CreateService(Group(x => x.Id = "new"), PublishedGroup(x => x.Id = "old"));
        service.SeedContent("new-draft", DraftContent);
        var operations = CreateOperations(service);
        var dryRun = new GroupedPagePublishOperationRequest { GroupId = "new", Publish = true, UnpublishConflicting = true, DryRun = true };
        var review = await operations.PublishAsync(dryRun, User, TestContext.Current.CancellationToken);
        var other = PublishedGroup(x => x.Id = "published");
        other.Pages[0].StoreId = StoreId;
        other.Pages[0].GroupId = other.Id;
        service.SeedGroup(other);
        var request = new GroupedPagePublishOperationRequest
        {
            GroupId = "new", Publish = true, UnpublishConflicting = true,
            ExpectedDraftVersion = review.Preview.Objects.Single(x => x.ObjectId == "new").ETag,
            ExpectedReplacedVersions = review.Preview.Objects.Where(x => x.ObjectId != "new").ToDictionary(x => x.ObjectId, x => x.ETag),
        };

        var result = await operations.PublishAsync(request, User, TestContext.Current.CancellationToken);

        Assert.Equal(SiteBuilderOperationStatus.Conflict, result.Status);
        Assert.Equal(Draft, (await service.GetByIdAsync("new")).Status);
        Assert.Equal(Published, (await service.GetByIdAsync("old")).Status);
        Assert.Equal(Published, (await service.GetByIdAsync("published")).Status);
    }

    [Fact]
    public async Task CreateGroup_WithPublishedPage_CreatesDraftOnly()
    {
        var service = CreateService(PublishedGroup(x => x.Id = "old"));
        var controller = CreateController(service);
        var model = PublishedGroup(x => x.Id = null);

        var result = await controller.CreateGroup(model, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
        var created = await service.GetByIdAsync(model.Id);
        Assert.Equal(Draft, Assert.Single(created.Pages).Status);
        Assert.Equal(Published, (await service.GetByIdAsync("old")).Status);
    }

    [Fact]
    public async Task GetPermalinkUsages_WithoutPage_ReturnsBadRequest()
    {
        var service = CreateService();
        var controller = CreateController(service);

        var result = await controller.GetPermalinkUsages(null, CreatePublishConflictService(service));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetPermalinkUsages_ChecksThePageAsSent()
    {
        var service = CreateService(PublishedGroup(x =>
        {
            x.Id = "old";
            x.OrganizationId = "org-a";
        }));
        var controller = CreateController(service);

        var result = await controller.GetPermalinkUsages(Group(x =>
        {
            x.Id = "new";
            x.OrganizationId = "org-b";
        }), CreatePublishConflictService(service));

        var usage = Assert.Single(Assert.IsType<IList<SiteBuilderPermalinkUsage>>(Assert.IsType<OkObjectResult>(result.Result).Value, exactMatch: false));
        Assert.Equal("old", usage.GroupId);
        Assert.Equal(SiteBuilderPermalinkConflict.None, usage.Conflict);
        Assert.True(usage.CanUnpublish);
    }

    private static GroupedSiteBuilderPage Group(Action<GroupedSiteBuilderPage> change = null)
    {
        var group = new GroupedSiteBuilderPage
        {
            Id = "group",
            StoreId = StoreId,
            Name = "Page",
            Permalink = "/about",
            CultureName = "en-US",
            Visibility = false,
        };
        change?.Invoke(group);
        group.Pages = group.Pages.Count > 0 ? group.Pages : [new SiteBuilderPage { Id = $"{group.Id}-draft", Status = Draft }];
        return group;
    }

    private static GroupedSiteBuilderPage PublishedGroup(Action<GroupedSiteBuilderPage> change = null)
    {
        return Group(x =>
        {
            change?.Invoke(x);
            x.Pages = x.Pages.Count > 0 ? x.Pages : [new SiteBuilderPage { Id = $"{x.Id}-published", Status = Published }];
        });
    }

    private static async Task<string> DraftVersionAsync(PublishedRenameContentPreservationTests.FakeGroupedPageService service, string groupId) =>
        (await service.LoadGroupContentAsync(await service.GetByIdAsync(groupId), draft: true, TestContext.Current.CancellationToken)).ETag;

    private static DateTime Date(int day) => new(2026, 1, day, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = new(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    private static PublishedRenameContentPreservationTests.FakeGroupedPageService CreateService(params GroupedSiteBuilderPage[] groups)
    {
        var service = new PublishedRenameContentPreservationTests.FakeGroupedPageService();
        foreach (var group in groups)
        {
            foreach (var page in group.Pages)
            {
                page.StoreId = group.StoreId;
                page.GroupId = group.Id;
            }
            service.SeedGroup(group);
        }
        return service;
    }

    private static SiteBuilderPageController CreateController(
        PublishedRenameContentPreservationTests.FakeGroupedPageService service,
        IGroupedPageSearchService searchService = null,
        IStoreService storeService = null)
    {
        var authorization = new PublishedRenameContentPreservationTests.AllowAllAuthorizationService();
        return new SiteBuilderPageController(
            service,
            searchService ?? new SeededGroupedPageSearchService(service),
            authorization,
            new PublishedRenameContentPreservationTests.NoopPageDocumentSearchService(),
            PageContent(service),
            NullLogger<SiteBuilderPageController>.Instance,
            CreateOperations(service, searchService, storeService),
            TestServices.PublishStatuses(authorization, service))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) },
            },
        };
    }

    private static GroupedPageOperationService CreateOperations(
        PublishedRenameContentPreservationTests.FakeGroupedPageService service,
        IGroupedPageSearchService searchService = null,
        IStoreService storeService = null)
    {
        return TestServices.GroupedPageOperations(
            new PublishedRenameContentPreservationTests.AllowAllAuthorizationService(),
            service,
            PageContent(service),
            new PublishedRenameContentPreservationTests.FakeSiteBuilderPageService(service),
            publishConflictService: CreatePublishConflictService(service, searchService, storeService));
    }

    private static SiteBuilderPublishConflictService CreatePublishConflictService(
        PublishedRenameContentPreservationTests.FakeGroupedPageService service,
        IGroupedPageSearchService searchService = null,
        IStoreService storeService = null)
    {
        return new SiteBuilderPublishConflictService(
            service,
            searchService ?? new SeededGroupedPageSearchService(service),
            storeService ?? new FakeStoreService(new Store { Id = StoreId }),
            PublishLock);
    }

    private static SiteBuilderPageContentService PageContent(PublishedRenameContentPreservationTests.FakeGroupedPageService service)
    {
        return new SiteBuilderPageContentService(
            new PublishedRenameContentPreservationTests.FakeSiteBuilderPageService(service),
            service,
            new PublishedRenameContentPreservationTests.NoopEventPublisher(),
            NullLogger<SiteBuilderPageContentService>.Instance);
    }

    // Returns every seeded group of the store; FindPermalinkUsagesAsync compares the permalinks itself.
    private sealed class SeededGroupedPageSearchService(PublishedRenameContentPreservationTests.FakeGroupedPageService service)
        : IGroupedPageSearchService
    {
        public async Task<GroupedSiteBuilderPageSearchResult> SearchAsync(SiteBuilderPageSearchCriteria criteria, bool clone = true)
        {
            var groups = (await service.GetAsync(SeededIds))
                .Where(x => x.StoreId == criteria.StoreId)
                .ToList();
            return new GroupedSiteBuilderPageSearchResult
            {
                TotalCount = groups.Count,
                Results = groups.Skip(criteria.Skip).Take(criteria.Take).ToList(),
            };
        }

        private static readonly string[] SeededIds = ["self", "new", "old", "published", "draft", "archived", "other-permalink"];
    }

    private sealed class FixedGroupedPageSearchService(IList<GroupedSiteBuilderPage> groups) : IGroupedPageSearchService
    {
        public Task<GroupedSiteBuilderPageSearchResult> SearchAsync(SiteBuilderPageSearchCriteria criteria, bool clone = true)
        {
            return Task.FromResult(new GroupedSiteBuilderPageSearchResult
            {
                TotalCount = groups.Count,
                Results = groups.Skip(criteria.Skip).Take(criteria.Take).ToList(),
            });
        }
    }

    // Holds each search until the other publish searches too, or a timeout passes, so unguarded publishes overlap.
    private sealed class BarrierGroupedPageSearchService(PublishedRenameContentPreservationTests.FakeGroupedPageService service, int parties)
        : IGroupedPageSearchService
    {
        private readonly SeededGroupedPageSearchService _inner = new(service);
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task<GroupedSiteBuilderPageSearchResult> SearchAsync(SiteBuilderPageSearchCriteria criteria, bool clone = true)
        {
            var result = await _inner.SearchAsync(criteria, clone);
            if (Interlocked.Increment(ref _arrived) == parties)
            {
                _allArrived.TrySetResult();
            }
            await Task.WhenAny(_allArrived.Task, Task.Delay(TimeSpan.FromMilliseconds(500)));
            return result;
        }
    }

    // Shared by the controllers of a test, like the platform lock is shared by requests.
    private static readonly InProcessDistributedLock PublishLock = new();

    private const string StoreId = "store";

    private const string DraftContent = """{"settings":{"name":"Page"},"content":[{"id":"a","type":"text"}]}""";

    private static readonly ClaimsPrincipal User = new(new ClaimsIdentity());
}
