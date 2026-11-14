using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.AssetsModule.Core.Assets;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class SearchAssetsToolTests : IAsyncDisposable
{
    private const string Tool = "search_assets";
    private const string Folder = "/stores/store-a/Page Builder";

    private readonly McpTestContext _context = new();
    private readonly List<BlobEntry> _entries =
    [
        new BlobFolder { Name = "banners", Type = "folder", RelativeUrl = $"{Folder}/banners" },
        .. Enumerable.Range(1, 4).Select(x => new BlobInfo { Name = $"image-{x}.png", Type = "blob", RelativeUrl = $"{Folder}/image-{x}.png", ContentType = "image/png", Size = 100 * x }),
    ];

    private SiteBuilderAssetReferencesSearchCriteria _lastReferenceCriteria;

    public SearchAssetsToolTests()
    {
        _context.AssetSearch
            .Setup(x => x.SearchAsync(It.IsAny<SiteBuilderAssetSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteBuilderAssetSearchCriteria criteria, CancellationToken _) => new SiteBuilderAssetSearchResult
            {
                TotalCount = _entries.Count,
                Skip = criteria.Skip,
                Results = _entries.Skip(criteria.Skip).Take(criteria.Take).ToList(),
            });

        // image-1 stands on a page and in a Shared Component of the store.
        _context.AssetReferences
            .Setup(x => x.SearchReferencesAsync(It.IsAny<SiteBuilderAssetReferencesSearchCriteria>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteBuilderAssetReferencesSearchCriteria criteria, CancellationToken _) =>
            {
                _lastReferenceCriteria = criteria;
                return new SiteBuilderAssetReferencesSearchResult
                {
                    Results = criteria.AssetUrls.Select(url => url.EndsWith("image-1.png")
                        ? new SiteBuilderAssetReference
                        {
                            AssetUrl = url,
                            NormalizedAssetUrl = SiteBuilderAssetReferenceMatcher.NormalizeAssetUrl(url),
                            ReferencesCount = 2,
                            PageReferencesCount = 1,
                            SharedComponentReferencesCount = 1,
                            Pages = [new SiteBuilderAssetReferencePage { Id = "a1", Name = "Spring sale" }],
                            SharedComponents = [new SiteBuilderAssetReferenceSharedComponent { Id = "c1", Name = "Footer" }],
                        }
                        : new SiteBuilderAssetReference { AssetUrl = url, NormalizedAssetUrl = SiteBuilderAssetReferenceMatcher.NormalizeAssetUrl(url) }).ToList(),
                };
            });
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Fact]
    public void TheTool_NeedsAssetRead_AsWellAsBuilderRead()
    {
        Assert.DoesNotContain(Tool, _context.Harness.GetVisibleToolNames(Reader()));
        Assert.Contains(Tool, _context.Harness.GetVisibleToolNames(AssetReader()));
    }

    [Fact]
    public async Task WithoutAStore_TheFilesAreListedWithoutTheirUse()
    {
        var page = await SearchAsync(AssetReader(), new { folder_url = Folder });

        Assert.Equal(5, page.TotalCount);
        Assert.Equal("folder", page.Items[0].Type);
        Assert.All(page.Items, x => Assert.Null(x.References));
        _context.AssetReferences.Verify(x => x.SearchReferencesAsync(It.IsAny<SiteBuilderAssetReferencesSearchCriteria>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WithAStore_EachFileSaysWhereThePagesUseIt_ButNotTheSharedComponents()
    {
        var page = await SearchAsync(AssetReader(), new { folder_url = Folder, store_id = StoreA });

        var image = page.Items.Single(x => x.Name == "image-1.png");
        Assert.Equal(2, image.References.Count);
        Assert.Equal("a1", image.References.Pages.Single().Id);
        Assert.Null(image.References.SharedComponentCount);
        Assert.Null(image.References.SharedComponents);
        Assert.Equal(0, page.Items.Single(x => x.Name == "image-2.png").References.Count);
        Assert.Null(page.Items.Single(x => x.Type == "folder").References);
        Assert.Equal(StoreA, _lastReferenceCriteria.StoreId);
        Assert.DoesNotContain($"{Folder}/banners", _lastReferenceCriteria.AssetUrls);
    }

    [Fact]
    public async Task WithSharedComponentsRead_TheComponentsAreNamedToo()
    {
        var user = AssetReader().WithPermissions(Permissions.SharedComponentsRead);

        var page = await SearchAsync(user, new { folder_url = Folder, store_id = StoreA });

        var image = page.Items.Single(x => x.Name == "image-1.png");
        Assert.Equal(1, image.References.SharedComponentCount);
        Assert.Equal("Footer", image.References.SharedComponents.Single().Name);
    }

    [Fact]
    public async Task AStoreReader_IsRefusedTheUseOfFilesInAnotherStore()
    {
        var user = McpTestUser.Create("store-a-assets")
            .WithScopedPermission(Permissions.Read, StoreScope, StoreA)
            .WithPermissions(AssetRead);

        var allowed = await _context.CallAsync(user, Tool, new { folder_url = Folder, store_id = StoreA });
        var refused = await _context.CallAsync(user, Tool, new { folder_url = Folder, store_id = StoreB });

        allowed.EnsureSucceeded();
        Assert.Equal(McpToolCallOutcome.Forbidden, refused.Outcome);
        Assert.Contains(StoreB, refused.Error);
    }

    [Fact]
    public async Task TwoPages_HoldEveryEntryOnce()
    {
        var first = await SearchAsync(AssetReader(), new { folder_url = Folder, take = 3 });
        var second = await SearchAsync(AssetReader(), new { folder_url = Folder, skip = first.NextSkip, take = 3 });

        Assert.True(first.HasMore);
        Assert.False(second.HasMore);
        Assert.Equal(_entries.Select(x => x.Name).Order(), first.Items.Concat(second.Items).Select(x => x.Name).Order());
    }

    [Fact]
    public async Task AnEmptyFolder_IsRefused_WithWhereToStart()
    {
        var result = await _context.CallAsync(AssetReader(), Tool, new { folder_url = " ", store_id = StoreA });

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("Page Builder", result.Error);
    }

    private static McpTestUser AssetReader() => McpTestUser.Create("asset-reader").WithPermissions(Permissions.Read, AssetRead);

    private async Task<McpPage<AssetRow>> SearchAsync(McpTestUser user, object arguments)
    {
        var result = await _context.CallAsync(user, Tool, arguments);

        return result.EnsureSucceeded().Read<McpPage<AssetRow>>();
    }
}
