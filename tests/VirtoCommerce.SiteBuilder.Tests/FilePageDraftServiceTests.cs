using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Moq;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class FilePageDraftServiceTests
{
    private readonly FakeBlobStorage _live = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DiscardBlobDraftAsync_PublishedPage_RemovesTheDraftAndRaisesTheEvent()
    {
        _live.Files["pages/s1/about.page"] = "live";
        _live.Files["pages/s1/about.page-draft"] = "draft";

        var status = await Service().DiscardBlobDraftAsync("s1", "pages", "about.page", theme: null, Ct);

        Assert.Equal(DraftDiscardStatus.Discarded, status);
        Assert.False(_live.Files.ContainsKey("pages/s1/about.page-draft"));
        Assert.Equal(["about.page"], _live.Events.OfType<SiteBuilderContentChangedEvent>().Single().ChangedEntries.Select(x => x.NewEntry.Path));
    }

    [Fact]
    public async Task DiscardBlobDraftAsync_PageNeverPublished_KeepsTheDraft()
    {
        _live.Files["pages/s1/about.page-draft"] = "draft";

        var status = await Service().DiscardBlobDraftAsync("s1", "pages", "about.page", theme: null, Ct);

        Assert.Equal(DraftDiscardStatus.OnlyCopy, status);
        Assert.True(_live.Files.ContainsKey("pages/s1/about.page-draft"));
        Assert.Empty(_live.Events);
    }

    [Fact]
    public async Task DiscardBlobDraftAsync_NoDraft_ChangesNothing()
    {
        _live.Files["pages/s1/about.page"] = "live";

        var status = await Service().DiscardBlobDraftAsync("s1", "pages", "about.page", theme: null, Ct);

        Assert.Equal(DraftDiscardStatus.NoDraft, status);
        Assert.Empty(_live.Events);
    }

    [Theory]
    [InlineData("draft", DraftDiscardStatus.Discarded, "live")]
    [InlineData(null, DraftDiscardStatus.NoDraft, null)]
    public async Task PreviewDiscardDraftAsync_StoreInGit_ReadsTheUserBranchAndTheBaseBranchOnly(string draft, DraftDiscardStatus status, string published)
    {
        var options = Options.Create(new GitContentOptions { BaseBranch = "master" });
        var repository = new Mock<IGitContentRepository>(MockBehavior.Strict);
        repository.Setup(x => x.ReadFileAsync("o/r", It.IsAny<string>(), It.Is<string>(b => b != "master"), It.IsAny<CancellationToken>())).ReturnsAsync(draft);
        repository.Setup(x => x.ReadFileAsync("o/r", It.IsAny<string>(), "master", It.IsAny<CancellationToken>())).ReturnsAsync("live");
        var service = TestServices.FilePageDrafts(_live, options, GitHubConnections.CreateService(options.Value), new FakeGitContentPolicy(), repository.Object);

        var preview = await service.PreviewDiscardDraftAsync("s1", "pages", "about.page",
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test")), Ct);

        Assert.Equal((status, draft, published), (preview.Status, preview.DraftContent, preview.PublishedContent));
        Assert.Empty(_live.Events);
    }

    private FilePageDraftService Service()
    {
        var options = Options.Create(new GitContentOptions());
        return TestServices.FilePageDrafts(_live, options, GitHubConnections.CreateService(options.Value), new FakeGitContentPolicy(enabled: false));
    }
}
