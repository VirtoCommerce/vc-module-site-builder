using System;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Web.Controllers.Api;
using Xunit;
using Fakes = VirtoCommerce.SiteBuilder.Tests.PublishedRenameContentPreservationTests;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Tests;

/// <summary>The raw content save reads the request body only once the page exists and the user may reach its store.</summary>
public class SavePageContentBodyReadTests
{
    private const string Content = """{"settings":{},"content":[]}""";

    private readonly Fakes.FakeGroupedPageService _pages = new();

    public SavePageContentBodyReadTests()
    {
        _pages.SeedGroup(new GroupedSiteBuilderPage
        {
            Id = "g1", StoreId = "s1", Name = "Home", Permalink = "/home",
            Pages = [new SiteBuilderPage { Id = "p1", StoreId = "s1", Status = Draft }],
        });
        _pages.SeedContent("p1", Content);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SavePageContent_PageOfAnotherUser_IsForbiddenWithoutReadingTheBody()
    {
        var body = new TrackingStream(Content);

        var result = await Controller(new FakeAuthorizationService("s2"), body).SavePageContent("g1", "\"v1\"", includeVersion: false, Ct);

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal((StatusCodes.Status403Forbidden, "{}"), (forbidden.StatusCode, PlatformJson.SerializeBody(forbidden)));
        Assert.False(body.WasRead);
    }

    [Fact]
    public async Task SavePageContent_UnknownPage_IsNotFoundWithoutReadingTheBody()
    {
        var body = new TrackingStream(Content);

        var result = await Controller(new FakeAuthorizationService("s1"), body).SavePageContent("missing", "\"v1\"", includeVersion: false, Ct);

        Assert.IsType<NotFoundResult>(result);
        Assert.False(body.WasRead);
    }

    [Fact]
    public async Task SavePageContent_PageOfTheUser_ReadsTheBodyAndChecksIt()
    {
        var body = new TrackingStream("{ not json");

        var result = await Controller(new FakeAuthorizationService("s1"), body).SavePageContent("g1", "\"v1\"", includeVersion: false, Ct);

        Assert.Equal("Page content must be a non-empty, well-formed JSON document.", Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.True(body.WasRead);
    }

    [Fact]
    public async Task SaveRawContentAsync_Refused_NeverCallsTheContentReader()
    {
        var reads = 0;
        var request = new GroupedPageContentSaveOperationRequest
        {
            GroupId = "g1",
            ContentReader = _ =>
            {
                reads++;
                return Task.FromResult(Content);
            },
        };

        var result = await Operations(new FakeAuthorizationService("s2")).SaveRawContentAsync(request, new ClaimsPrincipal(new ClaimsIdentity()), Ct);

        Assert.Equal((SiteBuilderOperationStatus.Forbidden, 0), (result.Status, reads));
    }

    private SiteBuilderPageController Controller(FakeAuthorizationService authorization, Stream body)
    {
        var controller = new SiteBuilderPageController(_pages, null, authorization, null, null, NullLogger<SiteBuilderPageController>.Instance,
            Operations(authorization), TestServices.PublishStatuses(authorization, _pages));
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        httpContext.Request.Body = body;
        httpContext.Response.Body = new MemoryStream();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return controller;
    }

    private GroupedPageOperationService Operations(FakeAuthorizationService authorization)
    {
        var pageService = new Fakes.FakeSiteBuilderPageService(_pages);
        var contentService = new SiteBuilderPageContentService(pageService, _pages, new Fakes.NoopEventPublisher(),
            NullLogger<SiteBuilderPageContentService>.Instance);

        return TestServices.GroupedPageOperations(authorization, _pages, contentService, pageService);
    }

    private sealed class TrackingStream(string content) : MemoryStream(Encoding.UTF8.GetBytes(content))
    {
        public bool WasRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            WasRead = true;
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            WasRead = true;
            return base.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            WasRead = true;
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WasRead = true;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
