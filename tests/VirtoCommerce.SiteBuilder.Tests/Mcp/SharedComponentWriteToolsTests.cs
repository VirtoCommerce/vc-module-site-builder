using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

// The writes run through the module's own shared component operation over the component services of the test.
public sealed class SharedComponentWriteToolsTests : IAsyncDisposable
{
    private const string Content = """{ "settings": {}, "content": [ { "id": "bar", "type": "announcement-bar", "text": "Free shipping" } ] }""";
    private static readonly McpToolCallOptions Approved = new() { AsApprovedChange = true };

    private readonly McpTestContext _context = new();
    private readonly List<SiteBuilderSharedComponent> _components = [Component("sc-a", StoreA), Component("sc-b", StoreB)];
    private readonly Dictionary<string, string> _contents = new() { ["sc-a"] = Content, ["sc-b"] = Content };
    private readonly List<string> _renamed = [];
    private SiteBuilderSharedComponent _created;

    public SharedComponentWriteToolsTests()
    {
        var pages = _context.UsePageStore();
        _context.SeedPage("home", StoreA, published: true, draft: false);

        _context.SharedComponents
            .Setup(x => x.GetAsync(It.IsAny<IList<string>>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((IList<string> ids, string _, bool _) => _components.Where(x => ids.Contains(x.Id)).Select(Copy).ToList());
        _context.SharedComponents
            .Setup(x => x.SaveWithContentAsync(It.IsAny<SiteBuilderSharedComponent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((SiteBuilderSharedComponent component, string content, CancellationToken _) =>
            {
                component.Id = "sc-new";
                _created = component;
                _contents[component.Id] = content;
            })
            .Returns(Task.CompletedTask);
        _context.SharedComponents
            .Setup(x => x.UpdateMetadataAsync(It.IsAny<SiteBuilderSharedComponent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteBuilderSharedComponent component, CancellationToken _) =>
            {
                _renamed.Add(component.Name);
                return component;
            });
        _context.SharedComponentContents
            .Setup(x => x.TryLoadContentAsync(It.IsAny<SiteBuilderSharedComponent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteBuilderSharedComponent component, CancellationToken _) => _contents.GetValueOrDefault(component.Id));
        _context.SharedComponentContents
            .Setup(x => x.TrySaveContentAsync(It.IsAny<SiteBuilderSharedComponent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteBuilderSharedComponent component, string content, CancellationToken _) =>
            {
                _contents[component.Id] = content;
                return true;
            });
        _context.SharedComponentUsages
            .Setup(x => x.GetUsageAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> ids, string _, bool _, CancellationToken _) => ids.Select(id => new SiteBuilderSharedComponentUsage
            {
                SharedComponentId = id,
                UsageCount = 2,
                Pages =
                [
                    new SiteBuilderSharedComponentUsagePage { Id = "home", Name = "Home", Status = "Published" },
                    new SiteBuilderSharedComponentUsagePage { Id = "sale", Name = "Sale", Status = "Draft" },
                ],
            }).ToList());
        _ = pages;
    }

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private static McpTestUser Creator() =>
        McpTestUser.Create("creator").WithPermissions(Permissions.Read, Permissions.SharedComponentsRead, Permissions.SharedComponentsCreate);

    private static McpTestUser Reviser() =>
        McpTestUser.Create("reviser").WithPermissions(Permissions.Read, Permissions.SharedComponentsRead, Permissions.SharedComponentsUpdate);

    private static McpTestUser StoreAReviser() =>
        McpTestUser.Create("store-a-reviser")
            .WithScopedPermission(Permissions.Read, StoreScope, StoreA)
            .WithScopedPermission(Permissions.SharedComponentsRead, StoreScope, StoreA)
            .WithPermissions(Permissions.SharedComponentsUpdate, Permissions.SharedComponentsCreate);

    [Fact]
    public void EachWrite_NeedsItsOwnPermission_AndSharedComponentsRead()
    {
        var reader = McpTestUser.Create("components").WithPermissions(Permissions.Read, Permissions.SharedComponentsRead);
        var withoutRead = McpTestUser.Create("no-read").WithPermissions(Permissions.Read, Permissions.SharedComponentsCreate, Permissions.SharedComponentsUpdate);
        string[] writes = ["create_shared_component", "revise_shared_component"];

        Assert.Equal(["create_shared_component"], _context.Harness.GetVisibleToolNames(Creator()).Intersect(writes));
        Assert.Equal(["revise_shared_component"], _context.Harness.GetVisibleToolNames(Reviser()).Intersect(writes));
        Assert.Empty(_context.Harness.GetVisibleToolNames(reader).Intersect(writes));
        Assert.Empty(_context.Harness.GetVisibleToolNames(withoutRead).Intersect(writes));
    }

    [Fact]
    public async Task ACreate_RunsAtOnce_FromSectionsAndCopiedOnes()
    {
        var result = await _context.CallAsync(Creator(), "create_shared_component", new
        {
            store_id = StoreA,
            name = "Spring banner",
            source_page_id = "home",
            sections = new[] { new { type = "text", settings = new { text = "Hello" } } },
        });

        var change = result.EnsureSucceeded().Read<SharedComponentChange>();
        Assert.Equal(McpToolCallOutcome.Succeeded, result.Outcome);
        Assert.Equal("sc-new", change.SharedComponentId);
        Assert.Equal(["hero-banner", "text"], change.Sections.Select(x => x.Type));
        Assert.Equal("Spring banner", _created.Name);
        Assert.Contains("Hello", _contents["sc-new"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACreateDryRun_CreatesNothing()
    {
        var change = (await _context.CallAsync(Creator(), "create_shared_component",
            new { store_id = StoreA, name = "Banner", sections = new[] { new { type = "text" } }, dryRun = true })).EnsureSucceeded().Read<SharedComponentChange>();

        Assert.True(change.DryRun);
        Assert.Contains(change.Changes, x => x.Field == "name" && x.To?.ToString() == "Banner");
        Assert.Null(_created);
    }

    [Fact]
    public async Task ACreateInAnotherStore_IsForbidden()
    {
        var result = await _context.CallAsync(StoreAReviser(), "create_shared_component", new { store_id = StoreB, name = "Banner", sections = new[] { new { type = "text" } } });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Null(_created);
    }

    [Fact]
    public async Task AContentChange_WaitsForApproval_AndListsThePagesItReaches()
    {
        var result = await _context.CallAsync(Reviser(), "revise_shared_component", new { shared_component_id = "sc-a", operations = new[] { SetText("Free returns") } });

        var change = result.Read<SharedComponentChange>();
        Assert.Equal(McpToolCallOutcome.PendingApproval, result.Outcome);
        Assert.True(change.AffectsPublished);
        Assert.Equal(["home", "sale"], change.PlacedOnPages);
        Assert.Contains(change.Changes, x => x.Field == "section announcement-bar 'bar': text" && x.To?.ToString() == "Free returns");
        Assert.Equal(Content, _contents["sc-a"]);
    }

    [Fact]
    public async Task AnApprovedContentChange_SavesTheContent()
    {
        var change = (await _context.CallAsync(Reviser(), "revise_shared_component",
            new { shared_component_id = "sc-a", operations = new[] { SetText("Free returns") } }, Approved)).EnsureSucceeded().Read<SharedComponentChange>();

        Assert.False(change.DryRun);
        Assert.Contains("Free returns", _contents["sc-a"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARename_RunsAtOnce_WithoutApproval()
    {
        var result = await _context.CallAsync(Reviser(), "revise_shared_component", new { shared_component_id = "sc-a", name = "Top bar" });

        var change = result.EnsureSucceeded().Read<SharedComponentChange>();
        Assert.False(change.DryRun);
        Assert.Contains(change.Changes, x => x.Field == "name" && x.From?.ToString() == "Component sc-a" && x.To?.ToString() == "Top bar");
        Assert.Equal(["Top bar"], _renamed);
        Assert.Equal(Content, _contents["sc-a"]);
    }

    [Fact]
    public async Task ARenameWithAContentChange_WaitsForApproval_AndRenamesNothing()
    {
        var result = await _context.CallAsync(Reviser(), "revise_shared_component",
            new { shared_component_id = "sc-a", name = "Top bar", operations = new[] { SetText("Free returns") } });

        Assert.Equal(McpToolCallOutcome.PendingApproval, result.Outcome);
        Assert.Empty(_renamed);
    }

    [Fact]
    public async Task AComponentOfAnotherStore_IsForbidden()
    {
        var result = await _context.CallAsync(StoreAReviser(), "revise_shared_component", new { shared_component_id = "sc-b", name = "Top bar" });

        Assert.Equal(McpToolCallOutcome.Forbidden, result.Outcome);
        Assert.Empty(_renamed);
    }

    [Fact]
    public async Task AComponent_HoldsNoOtherComponent()
    {
        var result = await _context.CallAsync(Reviser(), "revise_shared_component",
            new { shared_component_id = "sc-a", operations = new[] { new { op = "place_shared_component", shared_component_id = "sc-a" } } }, Approved);

        Assert.Equal(McpToolCallOutcome.Failed, result.Outcome);
        Assert.Contains("cannot hold a Shared Component", result.Error, StringComparison.Ordinal);
        Assert.Equal(Content, _contents["sc-a"]);
    }

    [Fact]
    public async Task UpdateSectionsBulk_NamesTheToolForComponentSections()
    {
        var tool = _context.Harness.GetTool("update_sections_bulk");

        Assert.Contains("revise_shared_component", tool.ProtocolTool.Description, StringComparison.Ordinal);
    }

    private static object SetText(string text) => new { op = "set_section_settings", section_id = "bar", settings = new { text } };

    private static SiteBuilderSharedComponent Component(string id, string storeId) => new() { Id = id, StoreId = storeId, Name = $"Component {id}" };

    private static SiteBuilderSharedComponent Copy(SiteBuilderSharedComponent component) =>
        new() { Id = component.Id, StoreId = component.StoreId, Name = component.Name };
}
