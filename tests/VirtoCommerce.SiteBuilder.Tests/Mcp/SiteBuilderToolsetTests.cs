using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.Platform.Core.Jobs;
using VirtoCommerce.SiteBuilder.MCP;
using VirtoCommerce.SiteBuilder.MCP.Jobs;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

public sealed class SiteBuilderToolsetTests : IAsyncDisposable
{
    private readonly McpTestContext _context = new();

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Fact]
    public void Toolset_FollowsTheConventions()
    {
        McpToolsetConventions.Verify<McpAssemblyMarker>(x => x
            .Ignore(McpConventionRules.Paging, "list_versions", "The git history pages by cursor: the answer carries next_cursor, and a skip would read every older version first."));
    }

    [Fact]
    public void AReaderOfEveryPart_SeesEveryReadTool()
    {
        var user = McpTestUser.Create("designer").WithPermissions(Permissions.Read, Permissions.SharedComponentsRead, AssetRead);

        Assert.Equal(
            ["list_versions", "search_assets", "search_shared_components", "search_site_pages"],
            _context.Harness.GetVisibleToolNames(user));
    }

    [Fact]
    public void BuilderRead_ShowsThePageAndHistoryTools_TheOthersNeedTheirOwnPermission()
    {
        Assert.Equal(["list_versions", "search_site_pages"], _context.Harness.GetVisibleToolNames(Reader()));
    }

    [Fact]
    public void BuilderRead_LimitedToAStore_ShowsTheSameTools()
    {
        Assert.Equal(["list_versions", "search_site_pages"], _context.Harness.GetVisibleToolNames(StoreAReader()));
    }

    [Fact]
    public void BuilderReadAndUpdate_ShowTheEditAndBulkTools()
    {
        var user = McpTestUser.Create("editor").WithPermissions(Permissions.Read, Permissions.Update);

        Assert.Equal(
            [
                "change_page_settings", "discard_draft", "edit_page_draft", "link_page_language", "list_versions", "reset_language_overrides",
                "search_site_pages", "set_translations", "unlink_page_language", "update_sections_bulk",
            ],
            _context.Harness.GetVisibleToolNames(user));
    }

    [Fact]
    public void BuilderCreateAndPublish_ShowTheCreateAndPublishingTools()
    {
        var user = McpTestUser.Create("publisher").WithPermissions(Permissions.Read, Permissions.Create, Permissions.Publish);

        Assert.Equal(["create_page", "list_versions", "publish_page", "search_site_pages", "unpublish_page"], _context.Harness.GetVisibleToolNames(user));
    }

    [Fact]
    public void TheRegistration_AddsTheJobsOfTheBulkTools()
    {
        var jobs = new ServiceCollection().AddSiteBuilderMcp()
            .Where(x => x.ServiceType == typeof(BackgroundJobDescriptor))
            .Select(x => ((BackgroundJobDescriptor)x.ImplementationInstance!).Name)
            .Order();

        Assert.Equal([nameof(SetTranslationsJob), nameof(UpdateSectionsBulkJob)], jobs);
    }

    [Fact]
    public void WithoutBuilderRead_NoToolIsVisible()
    {
        var user = McpTestUser.Create("assets-only").WithPermissions(Permissions.SharedComponentsRead, AssetRead, Permissions.Update);

        Assert.Empty(_context.Harness.GetVisibleToolNames(user));
    }
}
