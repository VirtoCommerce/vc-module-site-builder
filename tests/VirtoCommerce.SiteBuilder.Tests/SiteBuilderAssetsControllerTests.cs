using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.SiteBuilder.Web.Controllers.Api;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderAssetsControllerTests
{
    [Fact]
    public async Task SearchReferences_HidesSharedComponentMetadata_WithoutReadPermission()
    {
        var result = CreateSearchResult();
        var controller = CreateController(result, canReadSharedComponents: false);

        var response = await controller.SearchReferences(
            new SiteBuilderAssetReferencesSearchCriteria { StoreId = "store" },
            TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<SiteBuilderAssetReferencesSearchResult>(ok.Value);
        var reference = Assert.Single(payload.Results);

        Assert.Equal(5, reference.ReferencesCount);
        Assert.Equal(4, reference.PageReferencesCount);
        Assert.Equal(0, reference.SharedComponentReferencesCount);
        Assert.Empty(reference.SharedComponents);
    }

    [Fact]
    public async Task SearchReferences_ReturnsSharedComponentMetadata_WithReadPermission()
    {
        var result = CreateSearchResult();
        var controller = CreateController(result, canReadSharedComponents: true);

        var response = await controller.SearchReferences(
            new SiteBuilderAssetReferencesSearchCriteria { StoreId = "store" },
            TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<SiteBuilderAssetReferencesSearchResult>(ok.Value);
        var reference = Assert.Single(payload.Results);
        var component = Assert.Single(reference.SharedComponents);

        Assert.Equal(5, reference.ReferencesCount);
        Assert.Equal(1, reference.SharedComponentReferencesCount);
        Assert.Equal("component-1", component.Id);
        Assert.Equal("Shared hero", component.Name);
    }

    [Fact]
    public async Task SearchReferences_PermissionInAnotherStore_HidesSharedComponentMetadata()
    {
        var controller = CreateController(CreateSearchResult(), canReadSharedComponents: true);

        var response = await controller.SearchReferences(
            new SiteBuilderAssetReferencesSearchCriteria { StoreId = "other-store" },
            TestContext.Current.CancellationToken);

        var payload = Assert.IsType<SiteBuilderAssetReferencesSearchResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Empty(Assert.Single(payload.Results).SharedComponents);
    }

    private static SiteBuilderAssetsController CreateController(
        SiteBuilderAssetReferencesSearchResult result,
        bool canReadSharedComponents)
    {
        return new SiteBuilderAssetsController(
            new StubAssetReferenceService(result),
            new StubAuthorizationService(canReadSharedComponents))
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

    private static SiteBuilderAssetReferencesSearchResult CreateSearchResult()
    {
        return new SiteBuilderAssetReferencesSearchResult
        {
            TotalCount = 1,
            Results =
            [
                new SiteBuilderAssetReference
                {
                    AssetUrl = "/assets/hero.jpg",
                    ReferencesCount = 5,
                    PageReferencesCount = 4,
                    SharedComponentReferencesCount = 1,
                    SharedComponents =
                    [
                        new SiteBuilderAssetReferenceSharedComponent
                        {
                            Id = "component-1",
                            Name = "Shared hero",
                        },
                    ],
                },
            ],
        };
    }

    private sealed class StubAssetReferenceService(SiteBuilderAssetReferencesSearchResult result)
        : ISiteBuilderAssetReferenceService
    {
        public Task<SiteBuilderAssetReferencesSearchResult> SearchReferencesAsync(
            SiteBuilderAssetReferencesSearchCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result);
        }
    }

    private sealed class StubAuthorizationService(bool canReadSharedComponents) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object resource,
            IEnumerable<IAuthorizationRequirement> requirements)
        {
            var authorized = requirements.OfType<SiteBuilderAuthorizationRequirement>().All(x =>
                x.Permission != ModuleConstants.Security.Permissions.SharedComponentsRead ||
                (canReadSharedComponents && resource is IHasStoreId { StoreId: "store" }));
            return Task.FromResult(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }

        // A role scoped to a store holds no global permission, so a check made without the store fails.
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object resource,
            string policyName)
        {
            return Task.FromResult(AuthorizationResult.Failed());
        }
    }
}
