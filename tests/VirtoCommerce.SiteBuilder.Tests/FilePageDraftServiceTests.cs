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

    [Theory]
    [InlineData("draft")]
    [InlineData(null)]
    public async Task SaveDraftAsync_Blob_AtTheVersionRead_SavesTheDraft(string draft)
    {
        _live.Files["pages/s1/about.page"] = "live";
        if (draft != null)
        {
            _live.Files["pages/s1/about.page-draft"] = draft;
        }

        await Service().SaveDraftAsync("s1", Page(), SiteBuilderContentVersion.CreateForFileContent(draft ?? "live"), User, Ct);

        Assert.Contains("edited", _live.Files["pages/s1/about.page-draft"]);
    }

    [Fact]
    public async Task SaveDraftAsync_Blob_DraftSavedSinceItWasRead_IsAConflictAndWritesNothing()
    {
        _live.Files["pages/s1/about.page"] = "live";
        _live.Files["pages/s1/about.page-draft"] = "saved meanwhile";

        await Assert.ThrowsAsync<SiteBuilderContentConflictException>(() =>
            Service().SaveDraftAsync("s1", Page(), SiteBuilderContentVersion.CreateForFileContent("live"), User, Ct));

        Assert.Equal("saved meanwhile", _live.Files["pages/s1/about.page-draft"]);
        Assert.Empty(_live.Events);
    }

    /// <summary>The branch's file is compared, else what the read fell back to: what is published, else the blob copy.</summary>
    [Theory]
    [InlineData("draft", "live", "blob", "draft", true)]
    [InlineData("draft", "live", "blob", "live", false)]
    [InlineData(null, "live", "blob", "live", true)]
    [InlineData(null, null, "blob", "blob", true)]
    public async Task SaveDraftAsync_Git_CommitsOnlyOverTheFileRead(string onBranch, string published, string blob, string read, bool expected)
    {
        _live.Files["pages/s1/about.page"] = blob;
        var options = Options.Create(new GitContentOptions { BaseBranch = "master" });
        var repository = new Mock<IGitContentRepository>();
        repository.Setup(x => x.GetBranchHeadShaAsync("o/r", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("7222222222222222222222222222222222222222");
        repository.Setup(x => x.ReadFileAsync("o/r", It.IsAny<string>(), "master", It.IsAny<CancellationToken>())).ReturnsAsync(published);
        bool? isExpected = null;
        repository.Setup(x => x.CommitFileIfUnchangedAsync("o/r", "pages/about.page", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<GitCommitAuthor>(), It.IsAny<System.Func<string, Task<bool>>>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, string _, string _, string _, GitCommitAuthor _, System.Func<string, Task<bool>> check, CancellationToken _) =>
            {
                isExpected = await check(onBranch);
                return "7333333333333333333333333333333333333333";
            });
        var service = TestServices.FilePageDrafts(_live, options, GitHubConnections.CreateService(options.Value), new FakeGitContentPolicy(), repository.Object,
            new FakeGitContentHistory());

        await service.SaveDraftAsync("s1", Page(), SiteBuilderContentVersion.CreateForFileContent(read), User, Ct);

        Assert.Equal(expected, isExpected);
        repository.Verify(x => x.CommitFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<GitCommitAuthor>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    private static ClaimsPrincipal User => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test"));

    private static SiteBuilderContentFile Page() =>
        new() { ContentType = "pages", Path = "about.page", Content = Newtonsoft.Json.Linq.JToken.Parse("""{"settings":{"name":"edited"},"content":[]}""") };

    private FilePageDraftService Service()
    {
        var options = Options.Create(new GitContentOptions());
        return TestServices.FilePageDrafts(_live, options, GitHubConnections.CreateService(options.Value), new FakeGitContentPolicy(enabled: false));
    }
}
