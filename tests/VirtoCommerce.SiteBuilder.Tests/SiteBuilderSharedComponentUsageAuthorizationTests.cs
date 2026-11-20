using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.SiteBuilder.Web.Controllers.Api;
using VirtoCommerce.SiteBuilder.Web.Models;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderSharedComponentUsageAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Get_AlwaysReturnsUsageCount_ButGuardsPageDetails(bool canReadPages)
    {
        var component = new SiteBuilderSharedComponent
        {
            Id = "component-1",
            StoreId = "store",
            Name = "Shared hero",
        };
        var usageService = new StubUsageService();
        var controller = new SiteBuilderSharedComponentsController(
            new StubSharedComponentService(component),
            sharedComponentContentService: null,
            usageService,
            new StubAuthorizationService(canReadPages),
            TestServices.SharedComponentOperations(new StubAuthorizationService(canReadPages), new StubSharedComponentService(component),
                sharedComponentUsageService: usageService))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity()),
                },
            },
        };

        var response = await controller.Get(component.Id, TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<SiteBuilderSharedComponent>(ok.Value);
        Assert.Equal(2, payload.UsageCount);
        Assert.Equal(canReadPages, usageService.LastIncludePages);

        if (canReadPages)
        {
            Assert.Single(payload.UsagePages);
        }
        else
        {
            Assert.Empty(payload.UsagePages);
        }
    }

    [Fact]
    public async Task Get_RoleScopedToTheComponentsStore_ReturnsPageDetails()
    {
        var component = CreateAuthorizedComponent();
        var usageService = new StubUsageService();
        var controller = CreateController(
            new StubSharedComponentService(component),
            new StubSharedComponentContentService(),
            usageService,
            new FakeAuthorizationService("store", ModuleConstants.Security.Permissions.SharedComponentsRead));

        var response = await controller.Get(component.Id, TestContext.Current.CancellationToken);

        var payload = Assert.IsType<SiteBuilderSharedComponent>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.True(usageService.LastIncludePages);
        Assert.Single(payload.UsagePages);
    }

    [Fact]
    public async Task Update_WhenComponentWasDeletedAfterAuthorizationRead_ReturnsNotFound()
    {
        var component = new SiteBuilderSharedComponent
        {
            Id = "component-1",
            StoreId = "store",
            Name = "Shared hero",
        };
        var componentService = new StubSharedComponentService(component)
        {
            MetadataUpdateResult = null,
        };
        var controller = new SiteBuilderSharedComponentsController(
            componentService,
            sharedComponentContentService: null,
            new StubUsageService(),
            new StubAuthorizationService(canReadPages: true),
            TestServices.SharedComponentOperations(new StubAuthorizationService(canReadPages: true), componentService,
                sharedComponentUsageService: new StubUsageService()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity()),
                },
            },
        };

        var response = await controller.Update(
            component.Id,
            new SiteBuilderSharedComponentUpdateModel { Name = "Renamed hero" },
            TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response.Result);
    }

    [Fact]
    public async Task GetContent_WhenAuthorizedComponentWasReplaced_ReturnsNotFoundWithoutReadingById()
    {
        var component = CreateAuthorizedComponent();
        var contentService = new StubSharedComponentContentService
        {
            ConditionalLoadResult = null,
        };
        var controller = CreateController(
            new StubSharedComponentService(component),
            contentService,
            new StubUsageService());

        var response = await controller.GetContent(
            component.Id,
            TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response);
        Assert.Same(component, contentService.LastExpectedComponent);
        Assert.False(contentService.LegacyLoadCalled);
    }

    [Fact]
    public async Task SaveContent_WhenAuthorizedComponentWasReplaced_ReturnsNotFoundWithoutWritingById()
    {
        var component = CreateAuthorizedComponent();
        var contentService = new StubSharedComponentContentService
        {
            ConditionalSaveResult = false,
        };
        var controller = CreateController(
            new StubSharedComponentService(component),
            contentService,
            new StubUsageService());

        var response = await controller.SaveContent(
            component.Id,
            JObject.Parse("{ \"settings\": {}, \"content\": [] }"),
            TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response);
        Assert.Same(component, contentService.LastExpectedComponent);
        Assert.False(contentService.LegacySaveCalled);
    }

    [Fact]
    public async Task Delete_WhenAuthorizedComponentWasReplaced_ReturnsNotFoundWithoutDeletingById()
    {
        var component = CreateAuthorizedComponent();
        var componentService = new StubSharedComponentService(component)
        {
            ConditionalDeleteResult = false,
        };
        var controller = CreateController(
            componentService,
            new StubSharedComponentContentService(),
            new StubUsageService(usageCount: 0));

        var response = await controller.Delete(
            component.Id,
            TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(response);
        Assert.Same(component, componentService.LastDeleteExpectedComponent);
        Assert.False(componentService.LegacyDeleteCalled);
    }

    [Fact]
    public async Task SaveContent_ComponentOfAnotherStore_IsForbiddenBeforeTheMissingContent()
    {
        var component = CreateAuthorizedComponent();
        var contentService = new StubSharedComponentContentService { ConditionalSaveResult = true };

        var refused = await CreateController(new StubSharedComponentService(component), contentService, new StubUsageService(),
            new FakeAuthorizationService("another-store", ModuleConstants.Security.Permissions.SharedComponentsRead))
            .SaveContent(component.Id, null, TestContext.Current.CancellationToken);
        var invalid = await CreateController(new StubSharedComponentService(component), contentService, new StubUsageService())
            .SaveContent(component.Id, null, TestContext.Current.CancellationToken);

        var forbidden = Assert.IsType<ObjectResult>(refused);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal("{}", PlatformJson.SerializeBody(forbidden));
        Assert.Equal("Content is required.", Assert.IsType<BadRequestObjectResult>(invalid).Value);
        Assert.Null(contentService.SavedContent);
    }

    [Theory]
    [InlineData("Draft", false)]
    [InlineData("Draft, Published", true)]
    public async Task SaveContentOperation_DryRun_ListsThePagesItReaches_AndWritesNothing(string pageStatus, bool affectsPublished)
    {
        var component = CreateAuthorizedComponent();
        var contentService = new StubSharedComponentContentService { ConditionalLoadResult = """{"settings":{},"content":[]}""", ConditionalSaveResult = true };
        var operations = TestServices.SharedComponentOperations(new StubAuthorizationService(canReadPages: true), new StubSharedComponentService(component),
            contentService, new StubUsageService(pageStatus: pageStatus));

        var result = await operations.SaveContentAsync(ContentRequest(component, dryRun: true), User, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(affectsPublished, result.AffectsPublished);
        Assert.Null(contentService.SavedContent);
        Assert.Collection(result.Preview.Objects,
            change =>
            {
                Assert.Equal((ModuleConstants.ChangePreview.SharedComponent, component.Id), (change.ObjectType, change.ObjectId));
                var field = Assert.Single(change.Fields);
                Assert.Equal(("""{"settings":{},"content":[]}""", """{"settings":{},"content":[{"id":"hero","type":"text"}]}"""), (field.From, field.To));
            },
            page => Assert.Equal((ModuleConstants.ChangePreview.Page, "page-1"), (page.ObjectType, page.ObjectId)));
    }

    [Fact]
    public async Task SaveContentOperation_DryRun_TellsPagesTheUserMayNotReadByTheirNumberOnly()
    {
        var component = CreateAuthorizedComponent();
        var operations = TestServices.SharedComponentOperations(new StubAuthorizationService(canReadPages: false), new StubSharedComponentService(component),
            new StubSharedComponentContentService(), new StubUsageService(pageStatus: "Published"));

        var result = await operations.SaveContentAsync(ContentRequest(component, dryRun: true), User, TestContext.Current.CancellationToken);

        Assert.True(result.AffectsPublished);
        Assert.Equal(ModuleConstants.ChangePreview.SharedComponent, Assert.Single(result.Preview.Objects).ObjectType);
        Assert.Contains(result.Preview.Warnings, x => x.Contains("2 pages"));
    }

    [Theory]
    [InlineData("Draft", false)]
    [InlineData("Published", true)]
    public async Task SaveContentOperation_AskedForTheImpact_TellsWhetherAPublishedPageShowsTheContent(string pageStatus, bool affectsPublished)
    {
        var component = CreateAuthorizedComponent();
        var contentService = new StubSharedComponentContentService { ConditionalSaveResult = true };
        var operations = TestServices.SharedComponentOperations(new StubAuthorizationService(canReadPages: true), new StubSharedComponentService(component),
            contentService, new StubUsageService(pageStatus: pageStatus));
        var request = ContentRequest(component, dryRun: false);
        request.ReportImpact = true;

        var result = await operations.SaveContentAsync(request, User, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(affectsPublished, result.AffectsPublished);
        Assert.Equal("""{"settings":{},"content":[{"id":"hero","type":"text"}]}""", contentService.SavedContent);
    }

    [Fact]
    public async Task SaveContentOperation_NotAskedForTheImpact_SavesWithoutReadingWhereTheComponentIsPlaced()
    {
        var component = CreateAuthorizedComponent();
        var contentService = new StubSharedComponentContentService { ConditionalSaveResult = true };
        var usageService = new StubUsageService(pageStatus: "Published");
        var operations = TestServices.SharedComponentOperations(new StubAuthorizationService(canReadPages: true), new StubSharedComponentService(component),
            contentService, usageService);

        var result = await operations.SaveContentAsync(ContentRequest(component, dryRun: false), User, TestContext.Current.CancellationToken);

        Assert.Equal((true, false), (result.Succeeded, result.AffectsPublished));
        Assert.Equal(0, usageService.Calls);
        Assert.Equal("""{"settings":{},"content":[{"id":"hero","type":"text"}]}""", contentService.SavedContent);
    }

    [Fact]
    public async Task UpdateOperation_DryRun_AnswersTheNewName_AndKeepsTheComponent()
    {
        var component = CreateAuthorizedComponent();
        var componentService = new StubSharedComponentService(component);
        var operations = TestServices.SharedComponentOperations(new StubAuthorizationService(canReadPages: true), componentService,
            sharedComponentUsageService: new StubUsageService());

        var result = await operations.UpdateAsync(
            new SharedComponentUpdateOperationRequest { Id = component.Id, Name = "Renamed hero", DryRun = true }, User, TestContext.Current.CancellationToken);

        var field = Assert.Single(Assert.Single(result.Preview.Objects).Fields);
        Assert.Equal((ModuleConstants.ChangePreview.Name, "Shared hero", "Renamed hero"), (field.Name, field.From, field.To));
        Assert.False(result.AffectsPublished);
        Assert.Equal("Shared hero", component.Name);
        Assert.False(componentService.MetadataUpdated);
    }

    private static ClaimsPrincipal User => new(new ClaimsIdentity());

    private static SharedComponentContentSaveOperationRequest ContentRequest(SiteBuilderSharedComponent component, bool dryRun) => new()
    {
        Id = component.Id,
        Content = JObject.Parse("""{"settings":{},"content":[{"id":"hero","type":"text"}]}"""),
        DryRun = dryRun,
    };

    private static SiteBuilderSharedComponent CreateAuthorizedComponent()
    {
        return new SiteBuilderSharedComponent
        {
            Id = "component-1",
            StoreId = "store",
            Name = "Shared hero",
            CreatedDate = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc),
        };
    }

    private static SiteBuilderSharedComponentsController CreateController(
        ISiteBuilderSharedComponentService componentService,
        ISiteBuilderSharedComponentContentService contentService,
        ISiteBuilderSharedComponentUsageService usageService,
        IAuthorizationService authorizationService = null)
    {
        authorizationService ??= new StubAuthorizationService(canReadPages: true);
        return new SiteBuilderSharedComponentsController(
            componentService,
            contentService,
            usageService,
            authorizationService,
            TestServices.SharedComponentOperations(authorizationService, componentService, contentService, usageService))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity()),
                },
            },
        };
    }

    private sealed class StubSharedComponentService(SiteBuilderSharedComponent component)
        : ISiteBuilderSharedComponentService
    {
        public SiteBuilderSharedComponent MetadataUpdateResult { get; set; }
        public bool ConditionalDeleteResult { get; set; }
        public SiteBuilderSharedComponent LastDeleteExpectedComponent { get; private set; }
        public bool LegacyDeleteCalled { get; private set; }
        public bool MetadataUpdated { get; private set; }

        public Task<IList<SiteBuilderSharedComponent>> GetAsync(
            IList<string> ids,
            string responseGroup = null,
            bool clone = true)
        {
            IList<SiteBuilderSharedComponent> result = ids.Contains(component.Id) ? [component] : [];
            return Task.FromResult(result);
        }

        public Task SaveChangesAsync(IList<SiteBuilderSharedComponent> models)
        {
            throw new System.NotSupportedException();
        }

        public Task<SiteBuilderSharedComponent> UpdateMetadataAsync(
            SiteBuilderSharedComponent model,
            CancellationToken cancellationToken = default)
        {
            MetadataUpdated = true;
            return Task.FromResult(MetadataUpdateResult);
        }

        public Task SaveWithContentAsync(
            SiteBuilderSharedComponent model,
            string content,
            CancellationToken cancellationToken = default)
        {
            throw new System.NotSupportedException();
        }

        public Task<bool> TryDeleteAsync(
            SiteBuilderSharedComponent expectedComponent,
            CancellationToken cancellationToken = default)
        {
            LastDeleteExpectedComponent = expectedComponent;
            return Task.FromResult(ConditionalDeleteResult);
        }

        public Task DeleteAsync(IList<string> ids, bool softDelete = false)
        {
            LegacyDeleteCalled = true;
            throw new System.NotSupportedException();
        }
    }

    private sealed class StubSharedComponentContentService : ISiteBuilderSharedComponentContentService
    {
        public string ConditionalLoadResult { get; set; }
        public bool ConditionalSaveResult { get; set; }
        public SiteBuilderSharedComponent LastExpectedComponent { get; private set; }
        public bool LegacyLoadCalled { get; private set; }
        public bool LegacySaveCalled { get; private set; }
        public string SavedContent { get; private set; }

        public Task<string> LoadContentAsync(
            string sharedComponentId,
            CancellationToken cancellationToken = default)
        {
            LegacyLoadCalled = true;
            throw new System.NotSupportedException();
        }

        public Task<string> TryLoadContentAsync(
            SiteBuilderSharedComponent expectedComponent,
            CancellationToken cancellationToken = default)
        {
            LastExpectedComponent = expectedComponent;
            return Task.FromResult(ConditionalLoadResult);
        }

        public Task<IReadOnlyDictionary<string, string>> LoadContentsAsync(
            IEnumerable<string> sharedComponentIds,
            CancellationToken cancellationToken = default)
        {
            throw new System.NotSupportedException();
        }

        public Task SaveContentAsync(
            string sharedComponentId,
            string content,
            CancellationToken cancellationToken = default)
        {
            LegacySaveCalled = true;
            throw new System.NotSupportedException();
        }

        public Task<bool> TrySaveContentAsync(
            SiteBuilderSharedComponent expectedComponent,
            string content,
            CancellationToken cancellationToken = default)
        {
            LastExpectedComponent = expectedComponent;
            SavedContent = content;
            return Task.FromResult(ConditionalSaveResult);
        }
    }

    private sealed class StubUsageService(int usageCount = 2, string pageStatus = "Draft") : ISiteBuilderSharedComponentUsageService
    {
        public bool LastIncludePages { get; private set; }

        public int Calls { get; private set; }

        public Task<IList<SiteBuilderSharedComponentUsage>> GetUsageAsync(
            IEnumerable<string> sharedComponentIds,
            string storeId,
            bool includePages = true,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastIncludePages = includePages;
            IList<SiteBuilderSharedComponentUsage> result =
            [
                new SiteBuilderSharedComponentUsage
                {
                    SharedComponentId = "component-1",
                    UsageCount = usageCount,
                    // The controller must not trust service implementations to strip protected details.
                    Pages =
                    [
                        new SiteBuilderSharedComponentUsagePage
                        {
                            Id = "page-1",
                            Name = "Homepage",
                            Permalink = "homepage",
                            CultureName = "en-US",
                            Status = pageStatus,
                        },
                    ],
                },
            ];
            return Task.FromResult(result);
        }
    }

    private sealed class StubAuthorizationService(bool canReadPages) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object resource,
            IEnumerable<IAuthorizationRequirement> requirements)
        {
            var authorized = canReadPages || requirements.OfType<SiteBuilderAuthorizationRequirement>()
                .All(x => x.Permission != ModuleConstants.Security.Permissions.Read);
            return Task.FromResult(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object resource,
            string policyName)
        {
            return Task.FromResult(AuthorizationResult.Failed());
        }
    }
}
