using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Web.Controllers.Api;
using VirtoCommerce.SiteBuilder.Web.Models;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Services;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderSharedComponentCreateStoreTests
{
    [Theory]
    [InlineData("NO-SUCH-STORE-XYZ")]
    [InlineData(" store ")]
    public async Task Create_UnknownStore_ReturnsBadRequestWithoutSaving(string storeId)
    {
        var stores = new FakeStoreService(new Store { Id = "store" });
        var components = new RecordingComponentService();
        var controller = CreateController(stores, components);

        var response = await controller.Create(Request(storeId), TestContext.Current.CancellationToken);

        var error = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Equal($"Store '{storeId}' does not exist.", error.Value);
        Assert.Equal(storeId, stores.LastRequestedId);
        Assert.False(stores.LastClone);
        Assert.Null(components.SavedModel);
        Assert.Null(components.SavedContent);
    }

    [Fact]
    public async Task Create_ExistingStore_CreatesWithContentAndForwardsCancellation()
    {
        var stores = new FakeStoreService(new Store { Id = "store" });
        var components = new RecordingComponentService();
        var controller = CreateController(stores, components);
        var request = Request("store");
        var cancellationToken = TestContext.Current.CancellationToken;

        var response = await controller.Create(request, cancellationToken);

        var created = Assert.IsType<CreatedAtActionResult>(response.Result);
        var model = Assert.IsType<SiteBuilderSharedComponent>(created.Value);
        Assert.Equal(nameof(SiteBuilderSharedComponentsController.Get), created.ActionName);
        Assert.Equal("created-component", created.RouteValues["id"]);
        Assert.Same(components.SavedModel, model);
        Assert.Equal("store", model.StoreId);
        Assert.Equal(request.Name, model.Name);
        Assert.Equal(request.Content.ToString(Formatting.None), components.SavedContent);
        Assert.Equal(cancellationToken, components.CancellationToken);
    }

    [Fact]
    public async Task Create_UnauthorizedStore_ReturnsForbiddenBeforeStoreLookupOrSave()
    {
        var stores = new FakeStoreService(null);
        var components = new RecordingComponentService();
        var controller = CreateController(stores, components, authorized: false);

        var response = await controller.Create(Request("foreign-store"), TestContext.Current.CancellationToken);

        var forbidden = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Null(stores.LastRequestedId);
        Assert.Null(components.SavedModel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Create_MissingStoreId_ReturnsBadRequestWithoutLookupOrSave(string storeId)
    {
        var stores = new FakeStoreService(null);
        var components = new RecordingComponentService();
        var controller = CreateController(stores, components);

        var response = await controller.Create(Request(storeId), TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Null(stores.LastRequestedId);
        Assert.Null(components.SavedModel);
    }

    [Fact]
    public async Task Create_PublishedConstructor_ResolvesItsDependenciesFromRequestServices()
    {
        var stores = new FakeStoreService(new Store { Id = "store" });
        var components = new RecordingComponentService();
        using var services = new ServiceCollection()
            .AddSingleton<ISiteBuilderSharedComponentOperationService>(
                TestServices.SharedComponentOperations(new FakeAuthorizationService(true), components, storeService: stores))
            .BuildServiceProvider();
        var controller = new SiteBuilderSharedComponentsController(components, null, null, null,
            new FakeAuthorizationService(true))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = services },
            },
        };

        var response = await controller.Create(Request("store"), TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(response.Result);
        Assert.False(stores.LastClone);
    }

    [Fact]
    public async Task Create_ScriptUrl_ReturnsBadRequestWithoutSaving()
    {
        var components = new RecordingComponentService();
        var request = Request("store");
        request.Content = JObject.Parse("""{"settings":{},"content":[{"id":"hero","type":"text","link":"javascript:alert(1)"}]}""");

        var response = await CreateController(new FakeStoreService(new Store { Id = "store" }), components).Create(request, TestContext.Current.CancellationToken);

        Assert.Contains("shared component: 'content[0].link' holds a javascript: URL", (string)Assert.IsType<BadRequestObjectResult>(response.Result).Value);
        Assert.Null(components.SavedContent);
    }

    [Fact]
    public async Task Create_MarkdownHtml_SavesItSanitized()
    {
        var components = new RecordingComponentService();
        var request = Request("store");
        request.Content = JObject.Parse("""{"settings":{},"content":[{"id":"hero","type":"text","text":{"markdown":"x","html":"<p>x</p><script>alert(1)</script>"}}]}""");

        await CreateController(new FakeStoreService(new Store { Id = "store" }), components).Create(request, TestContext.Current.CancellationToken);

        Assert.Equal("""{"settings":{},"content":[{"id":"hero","type":"text","text":{"markdown":"x","html":"<p>x</p>"}}]}""", components.SavedContent);
    }

    [Fact]
    public async Task CreateOperation_DryRun_AnswersTheSanitizedComponent_AndSavesNothing()
    {
        var components = new RecordingComponentService();
        var operations = TestServices.SharedComponentOperations(new FakeAuthorizationService(true), components,
            storeService: new FakeStoreService(new Store { Id = "store" }));
        var request = new SharedComponentCreateOperationRequest
        {
            StoreId = "store",
            Name = "Shared hero",
            Content = JObject.Parse("""{"settings":{},"content":[{"id":"hero","type":"text","text":{"markdown":"x","html":"<p>x</p><script>alert(1)</script>"}}]}"""),
            DryRun = true,
        };

        var result = await operations.CreateAsync(request, new ClaimsPrincipal(new ClaimsIdentity()), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Null(result.Value);
        Assert.Null(components.SavedModel);
        var change = Assert.Single(result.Preview.Objects);
        Assert.Null(change.ObjectId);
        Assert.Equal("""{"settings":{},"content":[{"id":"hero","type":"text","text":{"markdown":"x","html":"<p>x</p>"}}]}""",
            Assert.Single(change.Fields, x => x.Name == ModuleConstants.ChangePreview.Content).To);
    }

    private static SiteBuilderSharedComponentCreateModel Request(string storeId) => new()
    {
        StoreId = storeId,
        Name = "Shared hero",
        Content = JObject.Parse("""{"settings":{},"content":[{"id":"hero","type":"text"}]}"""),
    };

    private static SiteBuilderSharedComponentsController CreateController(
        IStoreService stores, ISiteBuilderSharedComponentService components, bool authorized = true) =>
        new(components, null, null, new FakeAuthorizationService(authorized),
            TestServices.SharedComponentOperations(new FakeAuthorizationService(authorized), components, storeService: stores))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity()),
                },
            },
        };

    private sealed class RecordingComponentService : ISiteBuilderSharedComponentService
    {
        public SiteBuilderSharedComponent SavedModel { get; private set; }
        public string SavedContent { get; private set; }
        public CancellationToken CancellationToken { get; private set; }

        public Task SaveWithContentAsync(SiteBuilderSharedComponent model, string content, CancellationToken cancellationToken = default)
        {
            SavedModel = model;
            SavedContent = content;
            CancellationToken = cancellationToken;
            model.Id = "created-component";
            return Task.CompletedTask;
        }

        public Task<IList<SiteBuilderSharedComponent>> GetAsync(IList<string> ids, string responseGroup = null, bool clone = true) =>
            throw new NotSupportedException();
        public Task SaveChangesAsync(IList<SiteBuilderSharedComponent> models) => throw new NotSupportedException();
        public Task<SiteBuilderSharedComponent> UpdateMetadataAsync(SiteBuilderSharedComponent model, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> TryDeleteAsync(SiteBuilderSharedComponent expectedComponent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeleteAsync(IList<string> ids, bool softDelete = false) => throw new NotSupportedException();
    }

}
