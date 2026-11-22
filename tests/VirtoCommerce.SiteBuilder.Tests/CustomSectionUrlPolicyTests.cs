using System;
using Microsoft.Extensions.Logging.Abstractions;
using VirtoCommerce.SiteBuilder.Data.Theme;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class CustomSectionUrlPolicyTests
{
    private const string Assets = "https://cdn.example/assets/";

    [Fact]
    public void GetAssetsBaseUrl_OfALibraryThatFails_IsNone_AndNoImageIsAnAsset()
    {
        var policy = new CustomSectionUrlPolicy(new FailingAssetUrls(), NullLogger<CustomSectionUrlPolicy>.Instance);

        Assert.Null(policy.GetAssetsBaseUrl());
        Assert.False(policy.IsAssetUrl("https://cdn.example/assets/a.png"));
    }

    [Theory]
    [InlineData("https://cdn.example%2Fassets%2F@evil.example/x.png", false)]
    [InlineData("https://cdn.example/assets/a%2Fb.png", false)]
    [InlineData("https://cdn.example/ASSETS/a.png", false)]
    [InlineData("https://user@cdn.example/assets/a.png", false)]
    [InlineData("https://cdn.example/assets/\ta.png", false)]
    [InlineData("\u00a0https://cdn.example/assets/a.png", false)]
    [InlineData("https://cdn.example/assets/a.png\u00a0", true)]
    [InlineData("https://cdn.example:8443/assets/a.png", false)]
    [InlineData("https://cdn.example:443/assets/a.png", true)]
    [InlineData("HTTPS://CDN.EXAMPLE/assets/a.png", true)]
    [InlineData("https://cdn.example/assets/../assets/a.png", true)]
    [InlineData("https://cdn.example/assets/a.png", true)]
    public void IsAssetUrl_ReadsTheUrlAsWritten_AsTheStorefrontDoes(string url, bool isAsset)
    {
        Assert.Equal(isAsset, TestServices.CustomSectionUrlPolicy(Assets).IsAssetUrl(url));
    }

    [Theory]
    [InlineData("/\t/evil.example", false)]
    [InlineData("/\n/evil.example", false)]
    [InlineData("\u00a0/sale", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("//evil.example", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("#1st", false)]
    [InlineData("/sale", true)]
    [InlineData("/", true)]
    [InlineData("#reviews", true)]
    [InlineData("https://example.com/sale", true)]
    [InlineData("tel:+15551234567", true)]
    public void IsLink_TakesAPathAnAnchorOrAUrlOfTheSchemes_AndNoControlCharacter(string url, bool isLink)
    {
        Assert.Equal(isLink, TestServices.CustomSectionUrlPolicy(Assets).IsLink(url, CustomSectionTemplateValidator.LinkSchemes));
    }

    private sealed class FailingAssetUrls : VirtoCommerce.AssetsModule.Core.Assets.IBlobUrlResolver
    {
        public string GetAbsoluteUrl(string blobKey) => throw new InvalidOperationException("The asset provider is not configured.");
    }
}
