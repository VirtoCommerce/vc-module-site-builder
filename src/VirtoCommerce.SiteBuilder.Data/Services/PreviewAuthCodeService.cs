using System.Security.Claims;
using System.Security.Cryptography;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.Platform.Core;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Platform.Core.Settings;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public class PreviewAuthCodeService(
    IAuthorizationService authorizationService,
    IValidator<SiteBuilderStoreRequest> storeRequestValidator,
    ISettingsManager settingsManager,
    IDataProtectionProvider dataProtectionProvider,
    IMemoryCache memoryCache) : IPreviewAuthCodeService
{
    // Checking and remembering a nonce is one step, or two exchanges of one code at once could both pass.
    private static readonly Lock RedeemLock = new();

    private ITimeLimitedDataProtector Protector => dataProtectionProvider.CreateProtector(PreviewAuth.CodePurpose).ToTimeLimitedDataProtector();

    public virtual Task<SiteBuilderOperationResult<PreviewAuthCode>> CreateCodeAsync(PreviewAuthCodeOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return CreateCodeInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<PreviewAuthCode>> CreateCodeInternalAsync(PreviewAuthCodeOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var storeRequest = new SiteBuilderStoreRequest { StoreId = request.StoreId };
        if ((await authorizationService.CheckStoreOperationAsync(user, storeRequest)
             ?? await storeRequestValidator.CheckOperationRequestAsync(storeRequest, request.BindingErrors, cancellationToken)) is { } refused)
        {
            return refused.As<PreviewAuthCode>();
        }

        if (string.IsNullOrWhiteSpace(request.UserId))
        {
            return SiteBuilderOperationResult.Invalid("The request names no user to preview as.").As<PreviewAuthCode>();
        }

        // The impersonate grant names the operator the same way: the one already behind an impersonated session, else the caller.
        var operatorUserId = user?.FindFirstValue(PlatformConstants.Security.Claims.OperatorUserId).EmptyToNull() ?? user?.GetUserId();
        var operatorUserName = user?.FindFirstValue(PlatformConstants.Security.Claims.OperatorUserName).EmptyToNull() ?? user?.GetUserName();
        if (string.IsNullOrEmpty(operatorUserId) || !await IsPreviewUserAsync(request.StoreId, request.UserId))
        {
            return SiteBuilderOperationResult.Forbidden().As<PreviewAuthCode>();
        }

        var ticket = AbstractTypeFactory<PreviewAuthCodeTicket>.TryCreateInstance();
        ticket.UserId = request.UserId;
        ticket.StoreId = request.StoreId;
        ticket.OperatorUserId = operatorUserId;
        ticket.OperatorUserName = operatorUserName;
        ticket.Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        var code = AbstractTypeFactory<PreviewAuthCode>.TryCreateInstance();
        code.Code = Protector.Protect(JsonConvert.SerializeObject(ticket), PreviewAuth.CodeLifetime);
        code.ExpiresInSeconds = (int)PreviewAuth.CodeLifetime.TotalSeconds;

        return SiteBuilderOperationResult.Success(code);
    }

    public virtual Task<PreviewAuthCodeTicket> RedeemAsync(string code, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(RedeemInternal(code));
    }

    private PreviewAuthCodeTicket RedeemInternal(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        var ticket = AbstractTypeFactory<PreviewAuthCodeTicket>.TryCreateInstance();
        DateTimeOffset expiration;
        try
        {
            JsonConvert.PopulateObject(Protector.Unprotect(code, out expiration), ticket);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }

        if (string.IsNullOrEmpty(ticket.Nonce) || string.IsNullOrEmpty(ticket.UserId) || string.IsNullOrEmpty(ticket.StoreId))
        {
            return null;
        }

        // The cache is this instance's: another instance can take the code once more within its lifetime.
        var key = $"{nameof(PreviewAuthCodeService)}:{ticket.Nonce}";
        lock (RedeemLock)
        {
            if (memoryCache.TryGetValue(key, out _))
            {
                return null;
            }

            memoryCache.Set(key, true, expiration);
        }

        return ticket;
    }

    public virtual async Task<bool> IsPreviewUserAsync(string storeId, string userId)
    {
        if (string.IsNullOrEmpty(storeId) || string.IsNullOrEmpty(userId))
        {
            return false;
        }

        var setting = await settingsManager.GetObjectSettingAsync(ModuleConstants.Settings.StoreLevelSettings.PreviewUserIds.Name,
            ModuleConstants.Settings.StoreSettingsObjectType, storeId);

        // One id per line, as the designer lists them.
        return (setting?.Value?.ToString() ?? string.Empty)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(userId, StringComparer.Ordinal);
    }
}
