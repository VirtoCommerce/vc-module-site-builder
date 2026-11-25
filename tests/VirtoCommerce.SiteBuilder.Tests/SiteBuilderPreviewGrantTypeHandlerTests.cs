using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;
using OpenIddict.Abstractions;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Platform.Core.Security.Events;
using VirtoCommerce.Platform.Core.Security.SignInLog;
using VirtoCommerce.Platform.Security;
using VirtoCommerce.Platform.Security.OpenIddict;
using VirtoCommerce.StoreModule.Core.Model;
using Xunit;
using static OpenIddict.Abstractions.OpenIddictConstants;
using MvcSignInResult = Microsoft.AspNetCore.Mvc.SignInResult;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderPreviewGrantTypeHandlerTests
{
    private const string StoreId = "B2B-store";
    private const string TokenScheme = "OpenIddict.Server.AspNetCore";

    private readonly ApplicationUser _customer = new() { Id = "u1", UserName = "anna", StoreId = StoreId, UserType = nameof(UserType.Customer) };
    private readonly FakeSettingsManager _settings = new("u1");
    private readonly List<IEvent> _events = [];
    private readonly Mock<UserManager<ApplicationUser>> _userManager;
    private readonly Mock<SignInManager<ApplicationUser>> _signInManager;
    private readonly Mock<ITwoFactorSignInService> _twoFactor = new();
    private readonly PreviewAuthCodeService _codes;

    public SiteBuilderPreviewGrantTypeHandlerTests()
    {
        _userManager = new Mock<UserManager<ApplicationUser>>(Mock.Of<IUserStore<ApplicationUser>>(), null, null, null, null, null, null, null, null);
        _userManager.Setup(x => x.FindByIdAsync(_customer.Id)).ReturnsAsync(_customer);
        _userManager.Setup(x => x.IsLockedOutAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(false);

        _signInManager = new Mock<SignInManager<ApplicationUser>>(_userManager.Object, Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IUserClaimsPrincipalFactory<ApplicationUser>>(), null, null, null, null);
        _signInManager.Setup(x => x.CanSignInAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(true);
        _signInManager.Setup(x => x.CreateUserPrincipalAsync(It.IsAny<ApplicationUser>())).ReturnsAsync((ApplicationUser user) =>
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(Claims.Subject, user.Id), new Claim(Claims.Name, user.UserName)], TokenScheme)));

        _twoFactor.Setup(x => x.IsTwoFactorRequiredAsync(It.IsAny<ApplicationUser>())).ReturnsAsync(true);

        _codes = new PreviewAuthCodeService(new FakeAuthorizationService(true),
            new SiteBuilderStoreRequestValidator(new FakeStoreService(new Store { Id = StoreId })),
            _settings, new EphemeralDataProtectionProvider(), new MemoryCache(new MemoryCacheOptions()), () => _userManager.Object);
    }

    [Fact]
    public async Task Code_GivesTheCustomersTokenNamingTheEditor_WithoutRefreshTokenOrSignInSideEffects()
    {
        var result = await HandleAsync(await CodeAsync(), scope: "openid offline_access");

        var signIn = Assert.IsType<MvcSignInResult>(result);
        Assert.Equal(TokenScheme, signIn.AuthenticationScheme);
        Assert.Equal("u1", signIn.Principal.FindFirst(Claims.Subject)?.Value);
        Assert.Equal("op-1", signIn.Principal.FindFirst(PlatformConstants.Security.Claims.OperatorUserId)?.Value);
        Assert.Equal("editor", signIn.Principal.FindFirst(PlatformConstants.Security.Claims.OperatorUserName)?.Value);
        Assert.Contains(Destinations.AccessToken, signIn.Principal.FindFirst(PlatformConstants.Security.Claims.OperatorUserId)!.GetDestinations());
        Assert.Equal(new[] { Scopes.OpenId }, signIn.Principal.GetScopes().ToArray());

        _userManager.Verify(x => x.UpdateAsync(It.IsAny<ApplicationUser>()), Times.Never);
        Assert.DoesNotContain(_events, x => x is UserLoginEvent or BeforeUserLoginEvent);
        var attempt = Assert.Single(_events.OfType<UserSignInAttemptEvent>());
        Assert.True(attempt.Succeeded);
        Assert.Equal(ModuleConstants.Security.PreviewAuth.SignInType, attempt.SignInType);
        Assert.Equal("u1", attempt.UserId);
        Assert.Equal("op-1", attempt.OperatorUserId);
        Assert.Equal("editor", attempt.OperatorUserName);
    }

    [Fact]
    public async Task Code_UsedTwice_IsAnInvalidGrant()
    {
        var code = await CodeAsync();
        await HandleAsync(code);

        var result = await HandleAsync(code);

        AssertInvalidGrant(result, SignInFailureReason.Forbidden);
    }

    [Fact]
    public async Task Code_ForAUserTheStoreNoLongerLists_IsAnInvalidGrant()
    {
        var code = await CodeAsync();
        _settings.Values[ModuleConstants.Settings.StoreLevelSettings.PreviewUserIds.Name] = "u2";

        AssertInvalidGrant(await HandleAsync(code), SignInFailureReason.NotAllowed);
    }

    [Fact]
    public async Task Code_ForAUserMadeAnAdministratorSince_IsAnInvalidGrant()
    {
        var code = await CodeAsync();
        _customer.IsAdministrator = true;

        AssertInvalidGrant(await HandleAsync(code), SignInFailureReason.NotAllowed);
    }

    [Fact]
    public async Task Code_ForAUserSinceDeleted_IsAnInvalidGrant()
    {
        var code = await CodeAsync();
        _userManager.Setup(x => x.FindByIdAsync(_customer.Id)).ReturnsAsync((ApplicationUser)null);

        AssertInvalidGrant(await HandleAsync(code), SignInFailureReason.UserNotFound);
    }

    [Fact]
    public async Task Request_WithoutCode_IsRefused()
    {
        var result = await HandleAsync(null);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(SignInFailureReason.MissingParameter, Assert.Single(_events.OfType<UserSignInAttemptEvent>()).FailureReason);
    }

    [Fact]
    public async Task CookieSignIn_IsRefused_AndLeavesTheCodeUnused()
    {
        var code = await CodeAsync();

        var cookie = await HandleAsync(code, scheme: IdentityConstants.ApplicationScheme);

        var error = Assert.IsType<TokenResponse>(Assert.IsType<BadRequestObjectResult>(cookie).Value);
        Assert.Equal(Errors.UnsupportedGrantType, error.Error);
        Assert.IsType<MvcSignInResult>(await HandleAsync(code));
    }

    private async Task<string> CodeAsync()
    {
        var editor = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(PlatformConstants.Security.Claims.OperatorUserId, "op-1"),
            new Claim(PlatformConstants.Security.Claims.OperatorUserName, "editor"),
        ], "test"));

        return (await _codes.CreateCodeAsync(new PreviewAuthCodeOperationRequest { StoreId = StoreId, UserId = _customer.Id }, editor, TestContext.Current.CancellationToken)).Value.Code;
    }

    private Task<ActionResult> HandleAsync(string code, string scope = null, string scheme = TokenScheme)
    {
        var handler = new SiteBuilderPreviewGrantTypeHandler(_signInManager.Object, Options.Create(new IdentityOptions()), [], [], [],
            new RecordingEventPublisher(_events), _twoFactor.Object, _codes);

        var request = new OpenIddictRequest { GrantType = ModuleConstants.Security.GrantTypes.SiteBuilderPreview, Scope = scope };
        if (code != null)
        {
            request.SetParameter(SiteBuilderPreviewGrantTypeHandler.CodeParameter, code);
        }

        return handler.HandleAsync(new TokenRequestContext { AuthenticationScheme = scheme, Request = request });
    }

    private void AssertInvalidGrant(ActionResult result, string failureReason)
    {
        var error = Assert.IsType<TokenResponse>(Assert.IsType<BadRequestObjectResult>(result).Value);
        Assert.Equal(Errors.InvalidGrant, error.Error);
        Assert.Equal(failureReason, _events.OfType<UserSignInAttemptEvent>().Last().FailureReason);
    }

    private sealed class RecordingEventPublisher(List<IEvent> events) : IEventPublisher
    {
        public Task Publish<T>(T @event, CancellationToken cancellationToken = default) where T : IEvent
        {
            events.Add(@event);
            return Task.CompletedTask;
        }
    }
}
