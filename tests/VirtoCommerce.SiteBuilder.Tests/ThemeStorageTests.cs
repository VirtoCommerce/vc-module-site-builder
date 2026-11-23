using VirtoCommerce.SiteBuilder.Data.Theme;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class ThemeStorageTests
{
    [Theory]
    [InlineData("product.json", "product")]
    [InlineData("product.b2c.json", "product.b2c")]
    [InlineData("page.big-sale.json", "page.big-sale")]
    [InlineData("blog.json", null)]
    [InlineData("product.B2C.json", null)]
    [InlineData("product.json-draft", null)]
    public void GetTemplateKey_FileName_ReturnsKey(string fileName, string key)
    {
        Assert.Equal(key, ThemeStorage.GetTemplateKey(fileName));
    }

    [Theory]
    [InlineData("custom-hero", true)]
    [InlineData("custom-stats-band", true)]
    [InlineData("custom-hero\n", false)]
    [InlineData("custom-Hero", false)]
    public void IsCustomSectionType_Name_IsOneWhenItMatchesThePattern(string type, bool isType)
    {
        Assert.Equal(isType, ThemeStorage.IsCustomSectionType(type));
    }
}
