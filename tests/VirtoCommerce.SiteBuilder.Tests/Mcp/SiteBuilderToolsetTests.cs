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
        const string DraftOnly = "Changes a draft only: customers see it after a publication, which waits for approval, and restore_version brings back the earlier draft.";

        McpToolsetConventions.Verify<McpAssemblyMarker>(x => x
            .Ignore(McpConventionRules.Paging, "list_versions", "The git history pages by cursor: the answer carries next_cursor, and a skip would read every older version first.")
            .Ignore(McpConventionRules.Safeguard, "edit_page_draft", DraftOnly)
            .Ignore(McpConventionRules.Safeguard, "edit_template", DraftOnly)
            .Ignore(McpConventionRules.Safeguard, "edit_template_rules", DraftOnly)
            .Ignore(McpConventionRules.Safeguard, "edit_theme_draft", DraftOnly)
            .Ignore(McpConventionRules.Safeguard, "set_translations", DraftOnly + " A background job saves the pages, after a dry run of every item.")
            .Ignore(McpConventionRules.Safeguard, "update_sections_bulk", DraftOnly + " A background job saves the pages, after a dry run of the first ones."));
    }

    // Clients ask the user before a destructive call: every tool that can remove or overwrite content says so, and only
    // the tools that add something say they do not.
    [Theory]
    [InlineData("edit_page_draft")]
    [InlineData("update_sections_bulk")]
    [InlineData("edit_template")]
    [InlineData("edit_template_rules")]
    [InlineData("edit_theme_draft")]
    [InlineData("set_translations")]
    [InlineData("change_page_settings")]
    [InlineData("discard_draft")]
    [InlineData("restore_version")]
    [InlineData("revise_shared_component")]
    [InlineData("publish_page")]
    [InlineData("unpublish_page")]
    [InlineData("publish_theme")]
    [InlineData("convert_page_to_multilanguage")]
    [InlineData("link_page_language")]
    [InlineData("unlink_page_language")]
    [InlineData("reset_language_overrides")]
    public void AToolThatRemovesOrOverwrites_IsDestructive(string name)
    {
        var annotations = _context.Harness.GetTool(name).ProtocolTool.Annotations;

        Assert.False(annotations.ReadOnlyHint);
        Assert.True(annotations.DestructiveHint);
    }

    [Theory]
    [InlineData("create_page")]
    [InlineData("create_shared_component")]
    public void AToolThatOnlyAdds_IsNotDestructive(string name)
    {
        var annotations = _context.Harness.GetTool(name).ProtocolTool.Annotations;

        Assert.False(annotations.ReadOnlyHint);
        Assert.False(annotations.DestructiveHint);
        Assert.False(annotations.IdempotentHint);
    }

    [Fact]
    public void EveryTool_IsEitherReadOnlyOrListedAsAWrite()
    {
        string[] writes =
        [
            "change_page_settings", "convert_page_to_multilanguage", "create_page", "create_shared_component", "discard_draft", "edit_page_draft",
            "edit_template", "edit_template_rules", "edit_theme_draft", "link_page_language", "publish_page", "publish_theme", "reset_language_overrides",
            "restore_version", "revise_shared_component", "set_translations", "unlink_page_language", "unpublish_page", "update_sections_bulk",
        ];

        var readOnly = _context.Harness.Tools.Where(x => x.ProtocolTool.Annotations?.ReadOnlyHint == true).Select(x => x.ProtocolTool.Name).ToList();
        Assert.All(readOnly, x => Assert.Matches("^(get|search|list|find)_", x));
        Assert.Equal(writes, _context.Harness.Tools.Select(x => x.ProtocolTool.Name).Except(readOnly).Order());
    }

    [Fact]
    public void AReaderOfEveryPart_SeesEveryReadTool()
    {
        var user = McpTestUser.Create("designer").WithPermissions(Permissions.Read, Permissions.SharedComponentsRead, AssetRead);

        Assert.Equal(
            ["find_content_gaps", "get_preview", "get_publish_readiness", "get_site_overview", "list_versions", "search_assets", "search_shared_components", "search_site_pages", "search_templates"],
            _context.Harness.GetVisibleToolNames(user));
    }

    [Fact]
    public void BuilderRead_ShowsThePageAndHistoryTools_TheOthersNeedTheirOwnPermission()
    {
        Assert.Equal(["find_content_gaps", "get_preview", "get_publish_readiness", "get_site_overview", "list_versions", "search_site_pages", "search_templates"], _context.Harness.GetVisibleToolNames(Reader()));
    }

    [Fact]
    public void BuilderRead_LimitedToAStore_ShowsTheSameTools()
    {
        Assert.Equal(["find_content_gaps", "get_preview", "get_publish_readiness", "get_site_overview", "list_versions", "search_site_pages", "search_templates"], _context.Harness.GetVisibleToolNames(StoreAReader()));
    }

    // builder:update alone edits pages: the template and theme writes need builder:templates or builder:theme besides.
    [Fact]
    public void BuilderReadAndUpdate_ShowTheEditAndBulkTools()
    {
        var user = McpTestUser.Create("editor").WithPermissions(Permissions.Read, Permissions.Update);

        Assert.Equal(
            [
                "change_page_settings", "discard_draft", "edit_page_draft", "find_content_gaps", "get_preview", "get_publish_readiness", "get_site_overview",
                "link_page_language", "list_versions",
                "reset_language_overrides", "restore_version", "search_site_pages", "search_templates", "set_translations",
                "unlink_page_language", "update_sections_bulk",
            ],
            _context.Harness.GetVisibleToolNames(user));
    }

    [Fact]
    public void BuilderTemplates_AddsTheTemplateAndRuleEdits_BuilderTheme_TheThemeEdit()
    {
        var templates = McpTestUser.Create("templates").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Templates);
        var theme = McpTestUser.Create("theme").WithPermissions(Permissions.Read, Permissions.Update, Permissions.Theme);
        var withoutUpdate = McpTestUser.Create("no-update").WithPermissions(Permissions.Read, Permissions.Templates, Permissions.Theme);

        string[] designTools = ["edit_template", "edit_template_rules", "edit_theme_draft", "publish_theme"];
        Assert.Equal(["edit_template", "edit_template_rules"], _context.Harness.GetVisibleToolNames(templates).Intersect(designTools));
        Assert.Equal(["edit_theme_draft"], _context.Harness.GetVisibleToolNames(theme).Intersect(designTools));
        Assert.Empty(_context.Harness.GetVisibleToolNames(withoutUpdate).Intersect(designTools));
    }

    [Fact]
    public void BuilderCreateAndPublish_ShowTheCreateAndPublishingTools()
    {
        var user = McpTestUser.Create("publisher").WithPermissions(Permissions.Read, Permissions.Create, Permissions.Publish);

        Assert.Equal(
            ["create_page", "find_content_gaps", "get_preview", "get_publish_readiness", "get_site_overview", "list_versions", "publish_page", "publish_theme", "search_site_pages", "search_templates", "unpublish_page"],
            _context.Harness.GetVisibleToolNames(user));
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
