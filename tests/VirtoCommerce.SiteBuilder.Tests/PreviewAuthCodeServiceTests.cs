using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Newtonsoft.Json;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.StoreModule.Core.Model;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class PreviewAuthCodeServiceTests
{
    private const string StoreId = "B2B-store";
    private const string PreviewUsers = " u0\r\nu1 \n\n";

    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();

    private static readonly ApplicationUser[] Users =
    [
        new() { Id = "u0", UserType = nameof(UserType.Customer) },
        new() { Id = "u1", UserType = nameof(UserType.Customer) },
        new() { Id = "admin", UserType = nameof(UserType.Administrator), IsAdministrator = true },
        new() { Id = "manager", UserType = nameof(UserType.Manager) },
        new() { Id = "customer-admin", UserType = nameof(UserType.Customer), IsAdministrator = true },
        new() { Id = "no-type" },
    ];

    [Fact]
    public async Task CreateCode_ListedUser_GivesACodeRedeemedOnce()
    {
        var settings = new FakeSettingsManager(PreviewUsers);
        var service = CreateService(settings);

        var result = await service.CreateCodeAsync(Request("u1"), Editor(), TestContext.Current.CancellationToken);

        Assert.Equal(SiteBuilderOperationStatus.Succeeded, result.Status);
        Assert.Equal(60, result.Value.ExpiresInSeconds);
        Assert.DoesNotContain('+', result.Value.Code);
        Assert.DoesNotContain('/', result.Value.Code);
        Assert.Equal(ModuleConstants.Settings.StoreLevelSettings.PreviewUserIds.Name, settings.AskedName);
        Assert.Equal(ModuleConstants.Settings.StoreSettingsObjectType, settings.AskedObjectType);
        Assert.Equal(StoreId, settings.AskedObjectId);

        var ticket = await service.RedeemAsync(result.Value.Code, TestContext.Current.CancellationToken);
        Assert.NotNull(ticket);
        Assert.Equal("u1", ticket.UserId);
        Assert.Equal(StoreId, ticket.StoreId);
        Assert.Equal("op-1", ticket.OperatorUserId);
        Assert.Equal("editor", ticket.OperatorUserName);
        Assert.False(string.IsNullOrEmpty(ticket.Nonce));

        Assert.Null(await service.RedeemAsync(result.Value.Code, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Redeem_UsedCode_IsRefusedBySeparateServicesOfOneInstance()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var code = (await CreateService(cache: cache).CreateCodeAsync(Request("u0"), Editor(), TestContext.Current.CancellationToken)).Value.Code;

        Assert.NotNull(await CreateService(cache: cache).RedeemAsync(code, TestContext.Current.CancellationToken));
        Assert.Null(await CreateService(cache: cache).RedeemAsync(code, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateCode_UserTheStoreDoesNotList_IsForbidden()
    {
        var result = await CreateService().CreateCodeAsync(Request("u2"), Editor(), TestContext.Current.CancellationToken);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task CreateCode_ListedAdministrator_IsForbidden()
    {
        var service = CreateService(new FakeSettingsManager("admin"));

        var result = await service.CreateCodeAsync(Request("admin"), Editor(), TestContext.Current.CancellationToken);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task CreateCode_EditorWithPreviewAsInTheStore_GetsACode()
    {
        var service = CreateService(authorization: new FakeAuthorizationService(StoreId, ModuleConstants.Security.Permissions.PreviewAs));

        var result = await service.CreateCodeAsync(Request("u1"), Editor(), TestContext.Current.CancellationToken);

        Assert.Equal(SiteBuilderOperationStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task CreateCode_EditorWithPreviewAsInAnotherStoreOrOnlyRead_IsForbidden()
    {
        var anotherStore = CreateService(authorization: new FakeAuthorizationService("other", ModuleConstants.Security.Permissions.PreviewAs));
        var onlyRead = CreateService(authorization: new FakeAuthorizationService(StoreId, ModuleConstants.Security.Permissions.Read));

        Assert.Equal(SiteBuilderOperationStatus.Forbidden,
            (await anotherStore.CreateCodeAsync(Request("u1"), Editor(), TestContext.Current.CancellationToken)).Status);
        Assert.Equal(SiteBuilderOperationStatus.Forbidden,
            (await onlyRead.CreateCodeAsync(Request("u1"), Editor(), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task CreateCode_CallerWithoutUserId_IsForbidden()
    {
        var result = await CreateService().CreateCodeAsync(Request("u1"), new ClaimsPrincipal(new ClaimsIdentity()), TestContext.Current.CancellationToken);

        Assert.Equal(SiteBuilderOperationStatus.Forbidden, result.Status);
    }

    [Fact]
    public async Task CreateCode_UnknownStoreOrNoUser_IsInvalid()
    {
        var service = CreateService();

        var unknownStore = await service.CreateCodeAsync(new PreviewAuthCodeOperationRequest { StoreId = "other", UserId = "u1" }, Editor(), TestContext.Current.CancellationToken);
        var noUser = await service.CreateCodeAsync(Request(" "), Editor(), TestContext.Current.CancellationToken);

        Assert.Equal(SiteBuilderOperationStatus.Invalid, unknownStore.Status);
        Assert.Equal(SiteBuilderOperationStatus.Invalid, noUser.Status);
    }

    [Fact]
    public async Task Redeem_ExpiredCode_IsRefused()
    {
        var expired = _dataProtection.CreateProtector(ModuleConstants.Security.PreviewAuth.CodePurpose).ToTimeLimitedDataProtector()
            .Protect(JsonConvert.SerializeObject(Ticket()), DateTimeOffset.UtcNow.AddSeconds(-1));

        Assert.Null(await CreateService().RedeemAsync(expired, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Redeem_CodeOfAnotherPurposeOrAlteredOrNone_IsRefused()
    {
        var otherPurpose = _dataProtection.CreateProtector("another").ToTimeLimitedDataProtector()
            .Protect(JsonConvert.SerializeObject(Ticket()), TimeSpan.FromMinutes(1));
        var service = CreateService();

        Assert.Null(await service.RedeemAsync(otherPurpose, TestContext.Current.CancellationToken));
        Assert.Null(await service.RedeemAsync(otherPurpose[..^2] + "AA", TestContext.Current.CancellationToken));
        Assert.Null(await service.RedeemAsync("not a code", TestContext.Current.CancellationToken));
        Assert.Null(await service.RedeemAsync("", TestContext.Current.CancellationToken));
        Assert.Null(await service.RedeemAsync(null, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("u1", true)]
    [InlineData("u0", true)]
    [InlineData("U1", false)]
    [InlineData("u", false)]
    [InlineData("", false)]
    public async Task IsPreviewUser_OneIdPerLine(string userId, bool expected)
    {
        Assert.Equal(expected, await CreateService().IsPreviewUserAsync(StoreId, userId));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("manager")]
    [InlineData("customer-admin")]
    [InlineData("no-type")]
    [InlineData("missing")]
    public async Task IsPreviewUser_ListedAccountThatIsNotACustomer_IsRefused(string userId)
    {
        Assert.False(await CreateService(new FakeSettingsManager($"{userId}\nu1")).IsPreviewUserAsync(StoreId, userId));
    }

    [Fact]
    public async Task IsPreviewUser_StoreWithoutTheSetting_ListsNobody()
    {
        Assert.False(await CreateService(new FakeSettingsManager()).IsPreviewUserAsync(StoreId, "u1"));
    }

    private PreviewAuthCodeService CreateService(FakeSettingsManager settings = null, IMemoryCache cache = null, FakeAuthorizationService authorization = null) =>
        new(authorization ?? new FakeAuthorizationService(true),
            new SiteBuilderStoreRequestValidator(new FakeStoreService(new Store { Id = StoreId })),
            settings ?? new FakeSettingsManager(PreviewUsers),
            _dataProtection,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            UserManager);

    private static UserManager<ApplicationUser> UserManager()
    {
        var userManager = new Mock<UserManager<ApplicationUser>>(Mock.Of<IUserStore<ApplicationUser>>(), null, null, null, null, null, null, null, null);
        userManager.Setup(x => x.FindByIdAsync(It.IsAny<string>())).ReturnsAsync((string id) => Users.FirstOrDefault(x => x.Id == id));

        return userManager.Object;
    }

    private static PreviewAuthCodeOperationRequest Request(string userId) => new() { StoreId = StoreId, UserId = userId };

    private static PreviewAuthCodeTicket Ticket() => new() { UserId = "u1", StoreId = StoreId, OperatorUserId = "op-1", Nonce = "n" };

    // The platform's own claims, which the editor's principal carries while it is itself an impersonated session.
    private static ClaimsPrincipal Editor() => new(new ClaimsIdentity(
    [
        new Claim(PlatformConstants.Security.Claims.OperatorUserId, "op-1"),
        new Claim(PlatformConstants.Security.Claims.OperatorUserName, "editor"),
    ], "test"));
}
