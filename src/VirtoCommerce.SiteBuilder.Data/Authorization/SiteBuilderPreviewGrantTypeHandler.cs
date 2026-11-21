using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.Platform.Core;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Platform.Core.Security.SignInLog;
using VirtoCommerce.Platform.Security;
using VirtoCommerce.Platform.Security.Extensions;
using VirtoCommerce.Platform.Security.OpenIddict;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace VirtoCommerce.SiteBuilder.Data.Authorization;

/// <summary>
/// Exchanges a preview code for the preview user's access token. The storefront calls it on its own host, so the token's issuer is one
/// the storefront accepts. The token names the editor as its operator, as an impersonation token does.
/// </summary>
public class SiteBuilderPreviewGrantTypeHandler : GrantTypeHandlerBase
{
    public const string CodeParameter = "code";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPreviewAuthCodeService _previewAuthCodeService;
    private PreviewAuthCodeTicket _ticket;

    public SiteBuilderPreviewGrantTypeHandler(
        SignInManager<ApplicationUser> signInManager,
        IOptions<IdentityOptions> identityOptions,
        IEnumerable<ITokenRequestValidator> requestValidators,
        IEnumerable<ITokenClaimProvider> claimProviders,
        IEnumerable<ITokenRequestHandler> requestHandlers,
        IEventPublisher eventPublisher,
        ITwoFactorSignInService twoFactorSignInService,
        IPreviewAuthCodeService previewAuthCodeService)
        : base(signInManager, identityOptions, requestValidators, claimProviders, requestHandlers, eventPublisher, twoFactorSignInService)
    {
        _userManager = signInManager.UserManager;
        _previewAuthCodeService = previewAuthCodeService;
    }

    public override string GrantType => ModuleConstants.Security.GrantTypes.SiteBuilderPreview;

    protected override string SignInType => ModuleConstants.Security.PreviewAuth.SignInType;

    protected override async Task<GrantValidationResult> ValidateGrantAsync(TokenRequestContext context)
    {
        // The platform's cookie sign-in runs every grant too; a cookie session would outlive the token the preview is limited to.
        if (context.AuthenticationScheme == IdentityConstants.ApplicationScheme)
        {
            context.FailureReason = SignInFailureReason.NotAllowed;
            return GrantValidationResult.Fail(SecurityErrorDescriber.UnsupportedGrantType());
        }

        var missingParameters = new List<string>();
        var code = GetRequiredParameter(context.Request, CodeParameter, missingParameters);
        if (missingParameters.Count > 0)
        {
            return GrantValidationResult.Fail(BuildErrorResponse(context, missingParameters));
        }

        _ticket = _previewAuthCodeService.Redeem(code);
        if (_ticket == null)
        {
            context.FailureReason = SignInFailureReason.Forbidden;
            return GrantValidationResult.Fail(SecurityErrorDescriber.TokenInvalid());
        }

        var user = await _userManager.FindByIdAsync(_ticket.UserId);
        if (user == null)
        {
            context.FailureReason = SignInFailureReason.UserNotFound;
            return GrantValidationResult.Fail(SecurityErrorDescriber.TokenInvalid());
        }

        // The store can have stopped listing the user since the code was made.
        if (!await _previewAuthCodeService.IsPreviewUserAsync(_ticket.StoreId, user.Id))
        {
            context.FailureReason = SignInFailureReason.NotAllowed;
            return GrantValidationResult.Fail(SecurityErrorDescriber.TokenInvalid(), user);
        }

        return GrantValidationResult.Succeed(user);
    }

    // The editor signed in already; the customer's second factor is theirs, as with the impersonate grant.
    protected override Task<bool> IsTwoFactorRequiredAsync(TokenRequestContext context) => Task.FromResult(false);

    // A preview token is not renewed: the designer sends a new code.
    protected override void SetTicketScopes(TokenRequestContext context, ClaimsPrincipal principal)
    {
        base.SetTicketScopes(context, principal);
        principal.SetScopes(principal.GetScopes().Remove(Scopes.OfflineAccess));
    }

    protected override async Task<AuthenticationTicket> CreateTicketAsync(TokenRequestContext context)
    {
        var ticket = await base.CreateTicketAsync(context);

        var destinations = new[] { Destinations.AccessToken };
        ticket.Principal
            .SetClaimWithDestinations(PlatformConstants.Security.Claims.OperatorUserId, _ticket.OperatorUserId, destinations)
            .SetClaimWithDestinations(PlatformConstants.Security.Claims.OperatorUserName, _ticket.OperatorUserName, destinations);

        return ticket;
    }

    // An editor previewing as the customer is not the customer signing in.
    protected override Task BeforeSignInAsync(TokenRequestContext context) => Task.CompletedTask;

    protected override Task<TokenResponse> UpdateLastLoginAsync(TokenRequestContext context) => Task.FromResult<TokenResponse>(null);

    protected override Task AfterSignInAsync(TokenRequestContext context) => Task.CompletedTask;

    protected override UserSignInAttemptEvent BuildSignInAttemptEvent(TokenRequestContext context, bool succeeded)
    {
        var result = base.BuildSignInAttemptEvent(context, succeeded);
        result.OperatorUserId = _ticket?.OperatorUserId;
        result.OperatorUserName = _ticket?.OperatorUserName;

        return result;
    }
}
