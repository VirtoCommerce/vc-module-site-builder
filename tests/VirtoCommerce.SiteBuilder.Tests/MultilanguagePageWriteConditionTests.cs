using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.GenericCrud;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.MultilanguagePageServiceTests;

namespace VirtoCommerce.SiteBuilder.Tests;

/// <summary>
/// The conditions a reviewed write of a multilanguage page carries: they hold where the write takes its lock, and a write whose condition no
/// longer holds is a conflict that changes nothing. Without them each write runs as it always did.
/// </summary>
[Collection(nameof(MultilanguagePageServiceTests))]
public class MultilanguagePageWriteConditionTests
{
    private const string StoreId = "store";
    private const string Content = """{"settings":{},"content":[]}""";

    private readonly ClaimsPrincipal _user = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LinkAsync_RequireUnpublished_PublishedPage_IsAConflictAndLinksNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var landing = await fixture.CreateLanguageAsync(null, "nl-NL", permalink: "/over-ons", draftContent: Content);
        await fixture.PublishNewDraftAsync(landing.Id, content: null);

        var request = new MultilanguagePageLinkOperationRequest { MultilanguagePageId = root.Id, GroupId = landing.Id, RequireUnpublished = true };
        var result = await Operations(fixture).LinkAsync(request, _user, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Conflict, SiteBuilderContentConflictException.PublishedPageMessage), (result.Status, result.Error));
        Assert.Null((await fixture.LoadGroupAsync(landing.Id)).MultilanguagePageId);
    }

    [Fact]
    public async Task LinkAsync_RequireUnpublished_DraftOnlyPage_IsLinked()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var landing = await fixture.CreateLanguageAsync(null, "nl-NL", permalink: "/over-ons", draftContent: Content);

        var request = new MultilanguagePageLinkOperationRequest { MultilanguagePageId = root.Id, GroupId = landing.Id, RequireUnpublished = true };
        var result = await Operations(fixture).LinkAsync(request, _user, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal(root.Id, (await fixture.LoadGroupAsync(landing.Id)).MultilanguagePageId);
    }

    [Fact]
    public async Task UnlinkAsync_RequireUnpublished_PublishedVersion_IsAConflictAndKeepsTheLink()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var version = await fixture.CreateLanguageAsync(root, "en-US", draftContent: Content);
        await fixture.PublishNewDraftAsync(version.Id, content: null);

        var request = new MultilanguagePageVersionOperationRequest { GroupId = version.Id, RequireUnpublished = true };
        var result = await Operations(fixture).UnlinkAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Conflict, result.Status);
        Assert.Equal(root.Id, (await fixture.LoadGroupAsync(version.Id)).MultilanguagePageId);
    }

    [Fact]
    public async Task ConvertAsync_RequireUnpublished_PublishedPage_IsAConflictAndCreatesNoRoot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var landing = await fixture.CreateLanguageAsync(null, "nl-NL", permalink: "/over-ons", draftContent: Content);
        await fixture.PublishNewDraftAsync(landing.Id, content: null);

        var request = new MultilanguagePageVersionOperationRequest { GroupId = landing.Id, RequireUnpublished = true };
        var result = await Operations(fixture).ConvertAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Conflict, result.Status);
        Assert.Empty((await fixture.RootSearch.SearchAsync(new MultilanguagePageSearchCriteria { StoreId = StoreId })).Results);
        Assert.Null((await fixture.LoadGroupAsync(landing.Id)).MultilanguagePageId);
    }

    [Fact]
    public async Task ResetOverridesAsync_RequireUnpublished_PublishedVersionWouldChange_IsAConflictAndResetsNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var en = await fixture.CreateLanguageAsync(root, "en-US", permalink: "/about", draftContent: Content);
        var nl = await fixture.CreateLanguageAsync(root, "nl-NL", permalink: "/over-ons", draftContent: Content);
        await fixture.PublishNewDraftAsync(nl.Id, content: null);

        var request = new MultilanguagePageResetOverridesOperationRequest { MultilanguagePageId = root.Id, Fields = ["permalink"], RequireUnpublished = true };
        var result = await Operations(fixture).ResetOverridesAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Conflict, result.Status);
        Assert.Equal(("/about", "/over-ons"), ((await fixture.LoadGroupAsync(en.Id)).Permalink, (await fixture.LoadGroupAsync(nl.Id)).Permalink));
    }

    [Fact]
    public async Task ResetOverridesAsync_RequireUnpublished_VersionPublishedRightBeforeItsSave_IsRefusedUnderTheLock()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var nl = await fixture.CreateLanguageAsync(root, "nl-NL", permalink: "/over-ons", draftContent: Content);
        var roots = fixture.CreateRoots(new ConcurrentWriteContents(fixture.Groups, () => fixture.PublishNewDraftAsync(nl.Id, content: null)));

        var request = new MultilanguagePageResetOverridesOperationRequest { MultilanguagePageId = root.Id, Fields = ["permalink"], RequireUnpublished = true };
        var result = await Operations(fixture, roots).ResetOverridesAsync(request, _user, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Conflict, SiteBuilderContentConflictException.PublishedPageMessage), (result.Status, result.Error));
        Assert.Equal("/over-ons", (await fixture.LoadGroupAsync(nl.Id)).Permalink);
    }

    [Fact]
    public async Task ResetOverridesAsync_RequireUnpublished_DraftOnlyVersions_AreReset()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var nl = await fixture.CreateLanguageAsync(root, "nl-NL", permalink: "/over-ons", draftContent: Content);

        var request = new MultilanguagePageResetOverridesOperationRequest { MultilanguagePageId = root.Id, Fields = ["permalink"], RequireUnpublished = true };
        var result = await Operations(fixture).ResetOverridesAsync(request, _user, Ct);

        Assert.Equal(["nl-NL"], result.Value);
        Assert.Equal(root.Permalink, (await fixture.LoadGroupAsync(nl.Id)).Permalink);
    }

    [Fact]
    public async Task UpdateAsync_RequireUnpublished_PublishedVersionInheritsTheChange_IsAConflictAndWritesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var en = await fixture.CreateLanguageAsync(root, "en-US", draftContent: Content);
        await fixture.PublishNewDraftAsync(en.Id, content: null);

        var result = await Operations(fixture).UpdateAsync(Update(root, x => x.Visibility = false, requireUnpublished: true), _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Conflict, result.Status);
        Assert.True((await fixture.Roots.GetByIdAsync(root.Id)).Visibility);
        Assert.True((await fixture.LoadGroupAsync(en.Id)).Visibility);
    }

    [Fact]
    public async Task UpdateAsync_RequireUnpublished_VersionPublishedRightBeforeItsSave_IsRefusedUnderTheLock()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var en = await fixture.CreateLanguageAsync(root, "en-US", draftContent: Content);
        var roots = fixture.CreateRoots(new ConcurrentWriteContents(fixture.Groups, () => fixture.PublishNewDraftAsync(en.Id, content: null)));

        var result = await Operations(fixture, roots).UpdateAsync(Update(root, x => x.Visibility = false, requireUnpublished: true), _user, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Conflict, SiteBuilderContentConflictException.PublishedPageMessage), (result.Status, result.Error));
        Assert.True((await fixture.LoadGroupAsync(en.Id)).Visibility);
    }

    [Fact]
    public async Task UpdateAsync_RequireUnpublished_DraftOnlyVersions_AreUpdated()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var en = await fixture.CreateLanguageAsync(root, "en-US", draftContent: Content);

        var result = await Operations(fixture).UpdateAsync(Update(root, x => x.Visibility = false, requireUnpublished: true), _user, Ct);

        Assert.True(result.Succeeded);
        Assert.False((await fixture.LoadGroupAsync(en.Id)).Visibility);
    }

    [Fact]
    public async Task UpdateAsync_DryRun_TellsTheVersionOfTheRoot_AndTheUpdateAtItIsSaved()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var stored = await fixture.Roots.GetByIdAsync(root.Id);
        var dryRun = Update(root, x => x.Name = "About");
        dryRun.DryRun = true;

        var preview = (await Operations(fixture).UpdateAsync(dryRun, _user, Ct)).Preview;
        var request = Update(root, x => x.Name = "About");
        request.ExpectedSettingsVersion = preview.Objects[0].ETag;
        var result = await Operations(fixture).UpdateAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderContentVersion.CreateForMultilanguagePage(stored), preview.Objects[0].ETag);
        Assert.True(result.Succeeded);
        Assert.Equal("About", (await fixture.Roots.GetByIdAsync(root.Id)).Name);
    }

    [Fact]
    public async Task UpdateAsync_RootChangedSinceTheReview_IsAConflictAndKeepsTheOtherChange()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await fixture.CreateRootAsync();
        var reviewed = SiteBuilderContentVersion.CreateForMultilanguagePage(await fixture.Roots.GetByIdAsync(root.Id));
        await Operations(fixture).UpdateAsync(Update(root, x => x.Permalink = "/team"), _user, Ct);
        var request = Update(root, x => x.Name = "About");
        request.ExpectedSettingsVersion = reviewed;

        var result = await Operations(fixture).UpdateAsync(request, _user, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Conflict, SiteBuilderContentConflictException.SettingsChangedMessage), (result.Status, result.Error));
        var stored = await fixture.Roots.GetByIdAsync(root.Id);
        Assert.Equal(("About us", "/team"), (stored.Name, stored.Permalink));
    }

    [Fact]
    public async Task MarkUpToDateAsync_AtTheReviewedSourceRevision_IsMarked()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (en, nl) = await OutdatedTranslationAsync(fixture);
        var revision = await ReviewedSourceRevisionAsync(fixture, nl.Id);

        var request = new MultilanguagePageMarkUpToDateOperationRequest { GroupId = nl.Id, ExpectedSourceRevisionDate = revision };
        var result = await Operations(fixture).MarkUpToDateAsync(request, _user, Ct);

        Assert.True(result.Succeeded);
        Assert.Equal((await fixture.LoadGroupAsync(en.Id)).SourceRevisionDate, (await fixture.LoadGroupAsync(nl.Id)).SourceRevisionDate);
    }

    [Fact]
    public async Task MarkUpToDateAsync_SourcePublishedAgainSinceTheReview_IsAConflictAndWritesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (en, nl) = await OutdatedTranslationAsync(fixture);
        var revision = await ReviewedSourceRevisionAsync(fixture, nl.Id);
        var before = (await fixture.LoadGroupAsync(nl.Id)).SourceRevisionDate;
        await Task.Delay(20, Ct);
        await fixture.PublishNewDraftAsync(en.Id, Content);

        var request = new MultilanguagePageMarkUpToDateOperationRequest { GroupId = nl.Id, ExpectedSourceRevisionDate = revision };
        var result = await Operations(fixture).MarkUpToDateAsync(request, _user, Ct);

        Assert.Equal((SiteBuilderOperationStatus.Conflict, SiteBuilderContentConflictException.SourceRevisionChangedMessage), (result.Status, result.Error));
        Assert.Equal(before, (await fixture.LoadGroupAsync(nl.Id)).SourceRevisionDate);
    }

    [Fact]
    public async Task MarkUpToDateAsync_SourcePublishedAgainRightBeforeTheSave_IsRefusedUnderTheLock()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (en, nl) = await OutdatedTranslationAsync(fixture);
        var revision = await ReviewedSourceRevisionAsync(fixture, nl.Id);
        var before = (await fixture.LoadGroupAsync(nl.Id)).SourceRevisionDate;
        var roots = fixture.CreateRoots(new ConcurrentWriteContents(fixture.Groups, async () =>
        {
            await Task.Delay(20, Ct);
            await fixture.PublishNewDraftAsync(en.Id, Content);
        }));

        var request = new MultilanguagePageMarkUpToDateOperationRequest { GroupId = nl.Id, ExpectedSourceRevisionDate = revision };
        var result = await Operations(fixture, roots).MarkUpToDateAsync(request, _user, Ct);

        Assert.Equal(SiteBuilderOperationStatus.Conflict, result.Status);
        Assert.Equal(before, (await fixture.LoadGroupAsync(nl.Id)).SourceRevisionDate);
    }

    /// <summary>A translation published before its source was published again.</summary>
    private static async Task<(GroupedSiteBuilderPage En, GroupedSiteBuilderPage Nl)> OutdatedTranslationAsync(Fixture fixture)
    {
        var root = await fixture.CreateRootAsync();
        var en = await fixture.CreateLanguageAsync(root, "en-US", draftContent: Content);
        var nl = await fixture.CreateLanguageAsync(root, "nl-NL", permalink: "/over-ons", draftContent: Content);
        await fixture.Roots.PublishAllAsync(root.Id, Ct);
        await Task.Delay(20, Ct);
        await fixture.PublishNewDraftAsync(en.Id, Content);

        return (en, nl);
    }

    /// <summary>The source revision a dry run tells the translation will be based on.</summary>
    private async Task<DateTime> ReviewedSourceRevisionAsync(Fixture fixture, string groupId)
    {
        var dryRun = await Operations(fixture).MarkUpToDateAsync(new MultilanguagePageMarkUpToDateOperationRequest { GroupId = groupId, DryRun = true }, _user, Ct);
        var field = Assert.Single(dryRun.Preview.Objects).Fields.Single(x => x.Name == ModuleConstants.ChangePreview.SourceRevisionDate);

        return DateTime.Parse(field.To, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static MultilanguagePageSaveOperationRequest Update(MultilanguagePage root, Action<MultilanguagePage> change, bool requireUnpublished = false)
    {
        var model = new MultilanguagePage
        {
            Id = root.Id,
            StoreId = root.StoreId,
            Name = root.Name,
            Permalink = root.Permalink,
            DefaultCultureName = root.DefaultCultureName,
            Visibility = root.Visibility,
        };
        change(model);

        return new MultilanguagePageSaveOperationRequest { Page = model, RequireUnpublished = requireUnpublished };
    }

    private static MultilanguagePageOperationService Operations(Fixture fixture, MultilanguagePageService roots = null) =>
        new(new FakeAuthorizationService(StoreId), roots ?? fixture.Roots, fixture.Groups, fixture.StoreService);

    /// <summary>A write of someone else that lands right before a conditional save of a language version takes its lock.</summary>
    private sealed class ConcurrentWriteContents(GroupedPageService groups, Func<Task> concurrentWrite) : IGroupedPageContentService
    {
        public Task<SiteBuilderContentSnapshot> LoadGroupContentAsync(GroupedSiteBuilderPage authorizedGroup, bool draft = true,
            CancellationToken cancellationToken = default) =>
            groups.LoadGroupContentAsync(authorizedGroup, draft, cancellationToken);

        public Task<SiteBuilderConditionalContentWriteResult> SaveGroupContentAsync(GroupedSiteBuilderPage authorizedGroup, string content, string expectedETag,
            CancellationToken cancellationToken = default) =>
            groups.SaveGroupContentAsync(authorizedGroup, content, expectedETag, cancellationToken);

        public Task SaveGroupAtVersionAsync(GroupedSiteBuilderPage group, bool draft, string expectedETag, CancellationToken cancellationToken = default) =>
            groups.SaveGroupAtVersionAsync(group, draft, expectedETag, cancellationToken);

        public Task SaveGroupSettingsAsync(GroupedSiteBuilderPage group, CancellationToken cancellationToken = default) =>
            groups.SaveGroupSettingsAsync(group, cancellationToken);

        public async Task SaveGroupSettingsAsync(GroupedSiteBuilderPage group, GroupedPageSaveCondition condition, CancellationToken cancellationToken = default)
        {
            await concurrentWrite();
            await groups.SaveGroupSettingsAsync(group, condition, cancellationToken);
        }

        public async Task SaveGroupAsync(GroupedSiteBuilderPage group, GroupedPageSaveCondition condition, CancellationToken cancellationToken = default)
        {
            await concurrentWrite();
            await groups.SaveGroupAsync(group, condition, cancellationToken);
        }

        public Task<IList<string>> DeleteDraftsAtVersionAsync(GroupedSiteBuilderPage group, string expectedETag, CancellationToken cancellationToken = default) =>
            groups.DeleteDraftsAtVersionAsync(group, expectedETag, cancellationToken);
    }
}
