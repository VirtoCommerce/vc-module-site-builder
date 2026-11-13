using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using VirtoCommerce.CustomerModule.Core.Model;
using VirtoCommerce.CustomerModule.Core.Model.Search;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.Platform.Core;
using VirtoCommerce.Platform.Core.Settings;
using Xunit;
using static VirtoCommerce.SiteBuilder.Tests.Mcp.McpTestContext;
using CustomerPermissions = VirtoCommerce.CustomerModule.Core.ModuleConstants.Security.Permissions;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

// The resources and prompts as the MCP module serves them: a user sees those whose [McpPermission] they hold, and a
// resource is read with its address.
public sealed class SiteBuilderResourcesAndPromptsTests : IAsyncDisposable
{
    private const string SettingQuery = PlatformConstants.Security.Permissions.SettingQuery;

    private readonly McpTestContext _context = new();

    public ValueTask DisposeAsync() => _context.DisposeAsync();

    [Fact]
    public void ThePrompts_OfBothRolesAndTheReviewer_AreListed_ForAReader()
    {
        Assert.Equal(
            ["launch_landing_page", "rebrand_site", "review_site_change", "review_template_rules", "review_theme_release", "translate_site", "weekly_content_review"],
            VisiblePrompts(StoreAReader()));
        Assert.Empty(VisiblePrompts(McpTestUser.Create("none").WithPermissions(AssetRead)));
    }

    [Fact]
    public async Task APrompt_NumbersItsToolCalls_AndChangesNothingInAReview()
    {
        var prompt = Prompts().Single(x => x.ProtocolPrompt.Name == "weekly_content_review");

        var text = await GetPromptAsync(prompt, new Dictionary<string, JsonElement> { ["store_id"] = JsonSerializer.SerializeToElement(StoreA) });

        Assert.Contains("1. Call get_site_overview with store_id store-a", text, StringComparison.Ordinal);
        Assert.Contains("find_content_gaps", text, StringComparison.Ordinal);
        Assert.Contains("Change nothing", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResources_FollowTheirPermissions()
    {
        Assert.Equal(["section_schema", "section_schemas", "template_rule_conditions"], VisibleResources(Reader()));
        Assert.Equal(["section_schema", "section_schemas", "store_settings", "template_rule_conditions", "user_groups"],
            VisibleResources(McpTestUser.Create("settings").WithPermissions(Permissions.Read, SettingQuery)));
        Assert.Contains("organizations", VisibleResources(McpTestUser.Create("customers").WithPermissions(Permissions.Read, CustomerPermissions.Read)));
        Assert.Empty(VisibleResources(McpTestUser.Create("settings-only").WithPermissions(SettingQuery, CustomerPermissions.Read)));
    }

    [Fact]
    public async Task TheSchemas_OfAStore_ListTheTypesTheStorefrontSent()
    {
        await _context.SeedCatalogAsync(StoreA, ["hero-banner", "text"]);

        var schemas = await ReadAsync(Reader(), "site-builder://stores/store-a/schemas");

        Assert.True(schemas.GetProperty("available").GetBoolean());
        Assert.Equal(["hero-banner", "text"], schemas.GetProperty("kinds").GetProperty("sections").EnumerateArray().Select(x => x.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task TheSchemas_WithoutACatalog_AreNotAvailable_WithTheReason()
    {
        var schemas = await ReadAsync(Reader(), "site-builder://stores/store-b/schemas");

        Assert.False(schemas.GetProperty("available").GetBoolean());
        Assert.StartsWith("Not available:", schemas.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneSchema_IsReadByItsKindAndKey()
    {
        await _context.SeedCatalogAsync(StoreA, ["hero-banner"]);

        var schema = await ReadAsync(Reader(), "site-builder://stores/store-a/schemas/sections/hero-banner");
        var missing = await ReadAsync(Reader(), "site-builder://stores/store-a/schemas/sections/gallery");

        Assert.True(schema.GetProperty("available").GetBoolean());
        Assert.False(missing.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task TheSchemasOfAnotherStore_AreRefused()
    {
        await _context.SeedCatalogAsync(StoreB, ["hero-banner"]);

        await Assert.ThrowsAnyAsync<McpException>(() => ReadAsync(StoreAReader(), "site-builder://stores/store-b/schemas"));
    }

    [Fact]
    public async Task TheConditions_AreTheRulesPanelsOwn()
    {
        var conditions = await ReadAsync(Reader(), "site-builder://template-rule-conditions");

        Assert.Contains(conditions.GetProperty("conditions").EnumerateArray(), x => x.GetProperty("name").GetString() == "inCategory");
    }

    [Fact]
    public async Task TheStoreSettings_TellTheLanguagesAndTheFlow()
    {
        var settings = await ReadAsync(McpTestUser.Create("settings").WithPermissions(Permissions.Read, SettingQuery), "site-builder://stores/store-a/settings");

        Assert.Equal("en-US", settings.GetProperty("default_language").GetString());
        Assert.StartsWith("Not available:", settings.GetProperty("publication_flow_note").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUserGroups_ArePaged()
    {
        _context.Settings
            .Setup(x => x.GetObjectSettingAsync(It.IsAny<string>(), null, null))
            .ReturnsAsync(new ObjectSettingEntry { AllowedValues = ["Wholesale", "Retail", "VIP"] });
        var user = McpTestUser.Create("settings").WithPermissions(Permissions.Read, SettingQuery);

        var first = await ReadAsync(user, "site-builder://user-groups?skip=0&take=2");
        var second = await ReadAsync(user, "site-builder://user-groups?skip=2&take=2");

        Assert.Equal(3, first.GetProperty("total_count").GetInt32());
        Assert.Equal(["Retail", "VIP", "Wholesale"],
            first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(x => x.GetString()));
    }

    [Fact]
    public async Task TheOrganizations_NeedCustomerReadForEveryOrganization()
    {
        _context.Members
            .Setup(x => x.SearchMembersAsync(It.IsAny<MembersSearchCriteria>()))
            .ReturnsAsync(new MemberSearchResult { TotalCount = 1, Results = [new Organization { Id = "org-1", Name = "Contoso" }] });
        var everyOrganization = McpTestUser.Create("customers").WithPermissions(Permissions.Read, CustomerPermissions.Read);
        var someOrganizations = McpTestUser.Create("some").WithPermissions(Permissions.Read).WithScopedPermission(CustomerPermissions.Read, "AssociatedOrganizationsOnlyScope", "org-1");

        var organizations = await ReadAsync(everyOrganization, "site-builder://organizations?keyword=con");

        Assert.Equal("Contoso", organizations.GetProperty("items")[0].GetProperty("name").GetString());
        await Assert.ThrowsAnyAsync<McpException>(() => ReadAsync(someOrganizations, "site-builder://organizations"));
    }

    private IEnumerable<McpServerPrompt> Prompts() =>
        _context.Harness.Services.GetServices<McpServerPrompt>().Where(x => x.IsInMcpScope(McpScopes.Admin));

    private IReadOnlyList<string> VisiblePrompts(ClaimsPrincipal user) =>
        Prompts().Where(x => HasPermissions(user, x)).Select(x => x.ProtocolPrompt.Name).Order(StringComparer.Ordinal).ToList();

    private IReadOnlyList<string> VisibleResources(ClaimsPrincipal user) =>
        _context.Harness.Services.GetServices<McpServerResource>()
            .Where(x => x.IsInMcpScope(McpScopes.Admin) && HasPermissions(user, x))
            .Select(x => x.ProtocolResourceTemplate.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

    // As the MCP module lists prompts and resources: every [McpPermission] permission, in some store or in all.
    private static bool HasPermissions(ClaimsPrincipal user, IMcpServerPrimitive primitive) =>
        primitive.Metadata.OfType<McpPermissionAttribute>().SelectMany(x => x.Permissions).All(x => user.GetMcpPermissionGrant(x) != null);

    private async Task<JsonElement> ReadAsync(ClaimsPrincipal user, string uri)
    {
        var resource = _context.Harness.Services.GetServices<McpServerResource>().Single(x => x.IsMatch(uri));
        Assert.True(HasPermissions(user, resource));

        await using var scope = _context.Harness.Services.CreateAsyncScope();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, scope.ServiceProvider);
        var parameters = new ReadResourceRequestParams { Uri = uri };
        var request = new JsonRpcRequest
        {
            Id = new RequestId("1"),
            Method = RequestMethods.ResourcesRead,
            Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
        };
        var context = new RequestContext<ReadResourceRequestParams>(server, request, parameters)
        {
            Services = scope.ServiceProvider,
            User = user,
            MatchedPrimitive = resource,
        };

        var result = await resource.ReadAsync(context, TestContext.Current.CancellationToken);

        return JsonDocument.Parse(Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text).RootElement.Clone();
    }

    private async Task<string> GetPromptAsync(McpServerPrompt prompt, IDictionary<string, JsonElement> arguments)
    {
        await using var scope = _context.Harness.Services.CreateAsyncScope();
        await using var transport = new StreamServerTransport(Stream.Null, Stream.Null);
        await using var server = McpServer.Create(transport, new McpServerOptions(), NullLoggerFactory.Instance, scope.ServiceProvider);
        var parameters = new GetPromptRequestParams { Name = prompt.ProtocolPrompt.Name, Arguments = arguments };
        var request = new JsonRpcRequest
        {
            Id = new RequestId("1"),
            Method = RequestMethods.PromptsGet,
            Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
        };
        var context = new RequestContext<GetPromptRequestParams>(server, request, parameters) { Services = scope.ServiceProvider, MatchedPrimitive = prompt };

        var result = await prompt.GetAsync(context, TestContext.Current.CancellationToken);

        return string.Join(Environment.NewLine, result.Messages.Select(x => (x.Content as TextContentBlock)?.Text));
    }
}
