using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.MCP.Testing;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.JsonConverters;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.SiteBuilder.MCP;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Security;
using Permissions = VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.Tests.Mcp;

// The Site Builder toolset served by the MCP test harness: the module's real store authorization (the handler and its
// requirement) and history request validation, over services the test fills in.
internal sealed class McpTestContext : IAsyncDisposable
{
    public const string StoreA = "store-a";
    public const string StoreB = "store-b";
    public const string StoreC = "store-c";
    public const string StoreScope = nameof(StoreSelectedScope);
    public const string AssetRead = "platform:asset:read";

    // The store every user of the test is bound to, if any. The handler finds the user by the id claim, whose types
    // the platform sets at startup, so here every lookup answers the same account.
    private string _boundStoreId;
    private McpToolHarness _harness;

    public Mock<ISiteBuilderPageListService> PageList { get; } = new();
    public Mock<ISiteBuilderPageSearchService> PageVersionSearch { get; } = new();
    public Mock<IGroupedPageService> GroupedPages { get; } = new();
    public Mock<IGroupedPageContentService> PageContents { get; } = new();
    public Mock<ISiteBuilderAssetSearchService> AssetSearch { get; } = new();
    public Mock<ISiteBuilderAssetReferenceService> AssetReferences { get; } = new();
    public Mock<ISiteBuilderSharedComponentSearchService> SharedComponentSearch { get; } = new();
    public Mock<ISiteBuilderSharedComponentService> SharedComponents { get; } = new();
    public Mock<ISiteBuilderSharedComponentContentService> SharedComponentContents { get; } = new();
    public Mock<ISiteBuilderSharedComponentUsageService> SharedComponentUsages { get; } = new();
    public Mock<IContentHistory> History { get; } = new();

    public McpToolHarness Harness => _harness ??= McpToolHarness.Create<McpAssemblyMarker>(Register);

    public static McpTestUser Reader(string name = "reader") =>
        McpTestUser.Create(name).WithPermissions(Permissions.Read);

    // builder:read (and the other permissions) in store A only, through the store scope of a role.
    public static McpTestUser StoreAReader(string name = "store-a-reader") =>
        McpTestUser.Create(name).WithScopedPermission(Permissions.Read, StoreScope, StoreA);

    // An account bound to store A: global permissions still reach store A only.
    public McpTestUser StoreBoundReader(string name = "bound-reader")
    {
        BindAccountsTo(StoreA);
        return Reader(name);
    }

    public void BindAccountsTo(string storeId) => _boundStoreId = storeId;

    public ValueTask DisposeAsync() => _harness?.DisposeAsync() ?? ValueTask.CompletedTask;

    public Task<McpToolCallResult> CallAsync(McpTestUser user, string tool, object arguments) =>
        Harness.CallAsync(user, tool, arguments, cancellationToken: Xunit.TestContext.Current.CancellationToken);

    // The groups GetByIdAsync finds.
    public void AddGroups(params GroupedSiteBuilderPage[] groups)
    {
        GroupedPages
            .Setup(x => x.GetAsync(It.IsAny<IList<string>>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((IList<string> ids, string _, bool _) => groups.Where(x => ids.Contains(x.Id)).ToList());
    }

    public static GroupedSiteBuilderPage Group(string id, string storeId, string status = "Published") => new()
    {
        Id = id,
        StoreId = storeId,
        Name = $"Page {id}",
        Permalink = $"/{id}",
        CultureName = "en-US",
        CreatedDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        Pages = [new SiteBuilderPage { Id = $"{id}-version", GroupId = id, StoreId = storeId, Status = status }],
    };

    private void Register(IServiceCollection services)
    {
        RegisterPermissionScopes();

        var pageSize = new Mock<IMcpPageSizeResolver>();
        pageSize.Setup(x => x.GetPageSizeAsync(It.IsAny<int?>())).ReturnsAsync((int? take) => Math.Clamp(take ?? 20, 1, 50));

        services.AddAuthorizationCore();
        services.AddSingleton<Func<UserManager<ApplicationUser>>>(CreateUserManager);
        services.AddSingleton<IAuthorizationHandler>(new SiteBuilderAuthorizationHandler(CreateUserManager, Options.Create(CreateJsonOptions())));
        services.AddSingleton<IContentPermissionService>(provider => new ContentPermissionService(provider.GetRequiredService<IAuthorizationService>()));
        services.AddSingleton<IValidator<ContentHistoryRequest>>(new ContentHistoryRequestValidator(
            new SiteBuilderStoreRequestValidator(new FakeStoreService(new Store { Id = StoreA }, new Store { Id = StoreB }, new Store { Id = StoreC }))));

        services
            .AddSingleton(pageSize.Object)
            .AddSingleton(PageList.Object)
            .AddSingleton(PageVersionSearch.Object)
            .AddSingleton(GroupedPages.Object)
            .AddSingleton(PageContents.Object)
            .AddSingleton(AssetSearch.Object)
            .AddSingleton(AssetReferences.Object)
            .AddSingleton(SharedComponentSearch.Object)
            .AddSingleton(SharedComponents.Object)
            .AddSingleton(SharedComponentContents.Object)
            .AddSingleton(SharedComponentUsages.Object)
            .AddSingleton(History.Object);
    }

    private UserManager<ApplicationUser> CreateUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store
            .Setup(x => x.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => new ApplicationUser { Id = id, StoreId = _boundStoreId });

        return new UserManager<ApplicationUser>(store.Object, null, null, null, null, null, null, null, null);
    }

    // The store scope as the platform registers it, so the handler reads the scoped claims of McpTestUser.
    private static void RegisterPermissionScopes()
    {
        AbstractTypeFactory<PermissionScope>.RegisterType<StoreSelectedScope>();
        PolymorphJsonConverter.RegisterTypeForDiscriminator(typeof(PermissionScope), nameof(PermissionScope.Type));
    }

    private static MvcNewtonsoftJsonOptions CreateJsonOptions()
    {
        var options = new MvcNewtonsoftJsonOptions();
        options.SerializerSettings.Converters.Add(new PolymorphJsonConverter());
        options.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
        return options;
    }
}
