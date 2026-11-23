using System;
using System.Linq;
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

    [Fact]
    public void IsAssetUrl_OfManyUrlsWithALibraryThatFails_AsksTheLibraryOnce()
    {
        var assets = new FailingAssetUrls();
        var policy = new CustomSectionUrlPolicy(assets, NullLogger<CustomSectionUrlPolicy>.Instance);

        var anyAsset = Enumerable.Range(0, 100).Any(x => policy.IsAssetUrl($"https://cdn.example/assets/{x}.png"));

        Assert.False(anyAsset);
        Assert.Equal(1, assets.Calls);
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

    [Theory]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", true)]
    [InlineData("DATA:IMAGE/SVG+XML,<svg/>", true)]
    [InlineData("data:image/svg+xml,<svg><use href='#x'/></svg>", false)]
    [InlineData(" data:image/png;base64,iVBORw0KGgo=", false)]
    public void IsImageDataUrl_TakesAnImageInAnyCase_WithoutARawFragment(string url, bool isImage)
    {
        Assert.Equal(isImage, TestServices.CustomSectionUrlPolicy(Assets).IsImageDataUrl(url));
    }

    private sealed class FailingAssetUrls : VirtoCommerce.AssetsModule.Core.Assets.IBlobUrlResolver
    {
        public int Calls { get; private set; }

        public string GetAbsoluteUrl(string blobKey)
        {
            Calls++;
            throw new InvalidOperationException("The asset provider is not configured.");
        }
    }
}
