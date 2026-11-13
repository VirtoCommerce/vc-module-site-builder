using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.Platform.Core.Common;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.MultilanguagePageServiceTests;

namespace VirtoCommerce.SiteBuilder.Tests;

[Collection(nameof(MultilanguagePageServiceTests))]
public class MultilanguagePageOperationServiceTests
{
    private const string StoreId = "store";
    private const string Content = """{"settings":{},"content":[]}""";

    private static readonly string TooLongName = new('a', 1025);

    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpdateAsync_RootOfAnotherUser_IsForbiddenBeforeTheUpdateIsChecked()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();

        var request = new MultilanguagePageSaveOperationRequest { Page = new MultilanguagePage { Id = root.Id, Name = TooLongName, Permalink = "/about-us" } };
        var result = await Operations(fixture, new FakeAuthorizationService("other")).UpdateAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task CreateAsync_InvalidNameInAStoreOfAnotherUser_IsInvalidAsTheRootIsCheckedFirst()
    {
        await using var fixture = await Fixture.CreateAsync();

        var request = new MultilanguagePageSaveOperationRequest { Page = new MultilanguagePage { StoreId = StoreId, Name = TooLongName, Permalink = "/faq" } };
        var result = await Operations(fixture, new FakeAuthorizationService("other")).CreateAsync(request, _user, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Invalid, SiteBuilderPageName.InvalidMessage), (result.Status, result.Error));
    }

    [Fact]
    public async Task CreateAsync_DryRun_SavesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();

        var request = new MultilanguagePageSaveOperationRequest { Page = new MultilanguagePage { StoreId = StoreId, Name = "FAQ", Permalink = "/faq" }, DryRun = true };
        var result = await Operations(fixture).CreateAsync(request, _user, Ct);

        Assert.Equal((true, false), (result.Succeeded, result.AffectsPublished));
        var change = Assert.Single(result.Preview.Objects);
        Assert.Equal((ModuleConstants.ChangePreview.MultilanguagePage, null), (change.ObjectType, change.ObjectId));
        Assert.Contains(change.Fields, x => x.Name == ModuleConstants.ChangePreview.DefaultCultureName && x.To == "en-US");
        Assert.Empty((await fixture.RootSearch.SearchAsync(new MultilanguagePageSearchCriteria { StoreId = StoreId })).Results);
    }

    [Fact]
    public async Task LinkAsync_DraftOnlyPage_DoesNotAffectPublishedAndTheDryRunLinksNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var landing = await fixture.CreateLanguageAsync(null, "nl-NL", permalink: "/over-ons", draftContent: Content);

        var request = new MultilanguagePageLinkOperationRequest { MultilanguagePageId = root.Id, GroupId = landing.Id, DryRun = true };
        var dryRun = await Operations(fixture).LinkAsync(request, _user, Ct);

        Assert.Equal((true, false), (dryRun.Succeeded, dryRun.AffectsPublished));
        var change = Assert.Single(dryRun.Preview.Objects);
        Assert.Equal(landing.Id, change.ObjectId);
        Assert.Contains(change.Fields, x => x.Name == ModuleConstants.ChangePreview.MultilanguagePageId && x.From == null && x.To == root.Id);
        Assert.Null((await fixture.LoadGroupAsync(landing.Id)).MultilanguagePageId);
    }

    [Fact]
    public async Task LinkAsync_PublishedPage_AffectsPublishedOnTheDryRunAndTheWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var landing = await fixture.CreateLanguageAsync(null, "nl-NL", permalink: "/over-ons", draftContent: Content);
        await fixture.PublishNewDraftAsync(landing.Id, content: null);

        var request = new MultilanguagePageLinkOperationRequest { MultilanguagePageId = root.Id, GroupId = landing.Id, DryRun = true };
        var dryRun = await Operations(fixture).LinkAsync(request, _user, Ct);
        request.DryRun = false;
        var linked = await Operations(fixture).LinkAsync(request, _user, Ct);

        Assert.True(dryRun.AffectsPublished);
        Assert.Equal((true, true), (linked.Succeeded, linked.AffectsPublished));
        Assert.Equal(root.Id, (await fixture.LoadGroupAsync(landing.Id)).MultilanguagePageId);
    }

    [Fact]
    public async Task LinkAsync_PageInAnotherLanguageOfTheStore_IsInvalidOnTheDryRunAsOnTheWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var landing = await fixture.CreateLanguageAsync(null, "fr-FR", permalink: "/a-propos");

        var request = new MultilanguagePageLinkOperationRequest { MultilanguagePageId = root.Id, GroupId = landing.Id, DryRun = true };
        var dryRun = await Operations(fixture).LinkAsync(request, _user, Ct);
        request.DryRun = false;
        var linked = await Operations(fixture).LinkAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, dryRun.Status);
        Assert.Equal((dryRun.Status, dryRun.Error), (linked.Status, linked.Error));
    }

    [Fact]
    public async Task ConvertAsync_PublishedPage_AffectsPublishedAndTheDryRunCreatesNoRoot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var landing = await fixture.CreateLanguageAsync(null, "nl-NL", permalink: "/over-ons", draftContent: Content);
        await fixture.PublishNewDraftAsync(landing.Id, content: null);

        var result = await Operations(fixture).ConvertAsync(new GroupedPageOperationRequest { GroupId = landing.Id, DryRun = true }, _user, Ct);

        Assert.True(result.AffectsPublished);
        Assert.Equal([ModuleConstants.ChangePreview.MultilanguagePage, ModuleConstants.ChangePreview.Page], result.Preview.Objects.Select(x => x.ObjectType));
        Assert.Empty((await fixture.RootSearch.SearchAsync(new MultilanguagePageSearchCriteria { StoreId = StoreId })).Results);
        Assert.Null((await fixture.LoadGroupAsync(landing.Id)).MultilanguagePageId);
    }

    [Fact]
    public async Task ConvertAsync_DraftOnlyPage_DoesNotAffectPublished()
    {
        await using var fixture = await Fixture.CreateAsync();
        var landing = await fixture.CreateLanguageAsync(null, "nl-NL", permalink: "/over-ons", draftContent: Content);

        var result = await Operations(fixture).ConvertAsync(new GroupedPageOperationRequest { GroupId = landing.Id }, _user, Ct);

        Assert.Equal((true, false), (result.Succeeded, result.AffectsPublished));
        Assert.Equal(result.Value.Id, (await fixture.LoadGroupAsync(landing.Id)).MultilanguagePageId);
    }

    [Fact]
    public async Task UpdateAsync_PublishedVersionInheritsTheChange_AffectsPublishedAndTheDryRunChangesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var version = await fixture.CreateLanguageAsync(root, "en-US", draftContent: Content);
        await fixture.PublishNewDraftAsync(version.Id, content: null);

        var model = root.CloneTyped();
        model.Permalink = "/about";
        var result = await Operations(fixture).UpdateAsync(new MultilanguagePageSaveOperationRequest { Page = model, DryRun = true }, _user, Ct);

        Assert.True(result.AffectsPublished);
        var versionChange = Assert.Single(result.Preview.Objects, x => x.ObjectType == ModuleConstants.ChangePreview.Page);
        Assert.Equal(version.Id, versionChange.ObjectId);
        Assert.Contains(versionChange.Fields, x => x.Name == ModuleConstants.ChangePreview.Permalink && x.From == "/about-us" && x.To == "/about");
        Assert.Equal("/about-us", (await fixture.Roots.GetByIdAsync(root.Id)).Permalink);
        Assert.Equal("/about-us", (await fixture.LoadGroupAsync(version.Id)).Permalink);
    }

    [Fact]
    public async Task UpdateAsync_DraftOnlyVersions_DoNotAffectPublished()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        await fixture.CreateLanguageAsync(root, "en-US", draftContent: Content);

        var model = root.CloneTyped();
        model.Permalink = "/about";
        var result = await Operations(fixture).UpdateAsync(new MultilanguagePageSaveOperationRequest { Page = model }, _user, Ct);

        Assert.Equal((true, false), (result.Succeeded, result.AffectsPublished));
        Assert.Equal("/about", (await fixture.Roots.GetByIdAsync(root.Id)).Permalink);
    }

    [Fact]
    public async Task PublishAllAsync_DryRun_ListsTheDraftsAndPublishesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var english = await fixture.CreateLanguageAsync(root, "en-US", draftContent: Content);
        await fixture.CreateLanguageAsync(root, "nl-NL", permalink: "/over-ons");

        var request = new MultilanguagePageOperationRequest { MultilanguagePageId = root.Id, DryRun = true };
        var result = await Operations(fixture).PublishAllAsync(request, _user, Ct);

        Assert.True(result.AffectsPublished);
        var change = Assert.Single(result.Preview.Objects);
        Assert.Equal(english.Id, change.ObjectId);
        Assert.Equal(ModuleConstants.PageStatuses.Draft, (await fixture.Groups.GetByIdAsync(english.Id)).Status);
    }

    [Fact]
    public async Task PublishAllAsync_NoDrafts_DoesNotAffectPublished()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        await fixture.CreateLanguageAsync(root, "nl-NL", permalink: "/over-ons");

        var result = await Operations(fixture).PublishAllAsync(new MultilanguagePageOperationRequest { MultilanguagePageId = root.Id }, _user, Ct);

        Assert.Equal((true, false), (result.Succeeded, result.AffectsPublished));
        Assert.Empty(result.Value.ProcessedCultureNames);
    }

    [Fact]
    public async Task UnlinkAsync_DryRunOfAPublishedVersion_AffectsPublishedAndKeepsTheLink()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var version = await fixture.CreateLanguageAsync(root, "en-US", draftContent: Content);
        await fixture.PublishNewDraftAsync(version.Id, content: null);

        var result = await Operations(fixture).UnlinkAsync(new GroupedPageOperationRequest { GroupId = version.Id, DryRun = true }, _user, Ct);

        Assert.True(result.AffectsPublished);
        Assert.Equal(root.Id, (await fixture.LoadGroupAsync(version.Id)).MultilanguagePageId);
    }

    [Fact]
    public async Task CloneAsync_DryRun_CopiesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        await fixture.CreateLanguageAsync(root, "en-US", draftContent: Content);

        var result = await Operations(fixture).CloneAsync(new MultilanguagePageOperationRequest { MultilanguagePageId = root.Id, DryRun = true }, _user, Ct);

        Assert.Equal((true, false), (result.Succeeded, result.AffectsPublished));
        Assert.Equal([ModuleConstants.ChangePreview.MultilanguagePage, ModuleConstants.ChangePreview.Page], result.Preview.Objects.Select(x => x.ObjectType));
        Assert.Single((await fixture.RootSearch.SearchAsync(new MultilanguagePageSearchCriteria { StoreId = StoreId })).Results);
    }

    private static MultilanguagePageOperationService Operations(Fixture fixture, IAuthorizationService authorization = null) =>
        new(authorization ?? new FakeAuthorizationService(StoreId), fixture.Roots, fixture.Groups, fixture.StoreService);
}
