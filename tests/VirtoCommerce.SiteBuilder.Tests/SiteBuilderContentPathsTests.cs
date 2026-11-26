using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Validation;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests
{
    public class SiteBuilderContentPathsTests
    {
        [Theory]
        [InlineData("abcdefgh", true)]
        [InlineData("abcdefgh\n", false)]
        public void CatalogHash_EndingInALineFeed_IsNotOne(string hash, bool expected) => Assert.Equal(expected, SiteBuilderContentPaths.IsCatalogHash(hash));

        [Theory]
        [InlineData("save-0001", true)]
        [InlineData("save-0001\n", false)]
        public void SaveId_EndingInALineFeed_IsNotOne(string saveId, bool expected) => Assert.Equal(expected, SiteBuilderContentPaths.IsSaveId(saveId));

        [Theory]
        [InlineData("abcdefghijklmnopqrstuvwxyz0", true)]
        [InlineData("abcdefghijklmnopqrstuvwxyz0\n", false)]
        public void HistoryVersionId_EndingInALineFeed_IsNotOne(string versionId, bool expected) =>
            Assert.Equal(expected, SiteBuilderContentPaths.IsHistoryVersionId(versionId));

        [Theory]
        [InlineData("pages")]
        [InlineData("blogs")]
        [InlineData("themes")]
        [InlineData("Pages")]
        public void KnownContentTypes_AreAccepted(string type) => Assert.True(SiteBuilderContentPaths.IsContentType(type));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("assets")]
        [InlineData("..")]
        [InlineData("pages/..")]
        public void OtherContentTypes_AreRefused(string type) => Assert.False(SiteBuilderContentPaths.IsContentType(type));

        [Theory]
        [InlineData("about-us.page")]
        [InlineData("/about-us.page")]
        [InlineData("blogs/news/first.page")]
        [InlineData("about-us.page-draft")]
        [InlineData("default/config/settings_data.json")]
        public void PathsInsideTheContentFolder_AreAccepted(string path) => Assert.True(SiteBuilderContentPaths.IsRelativePath(path));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("/")]
        [InlineData("../about-us.page")]
        [InlineData("blogs/../../about-us.page")]
        [InlineData("./about-us.page")]
        [InlineData("blogs//about-us.page")]
        [InlineData("\\about-us.page")]
        [InlineData("blogs\\..\\about-us.page")]
        [InlineData("C:/Windows/win.ini")]
        [InlineData("about-us.page\0")]
        [InlineData("default/config./builder_settings.json")]
        [InlineData("default/config /builder_settings.json")]
        [InlineData(".. /about-us.page")]
        public void PathsThatLeaveTheContentFolder_AreRefused(string path) => Assert.False(SiteBuilderContentPaths.IsRelativePath(path));

        [Theory]
        [InlineData("default", true)]
        [InlineData("b2b-theme", true)]
        [InlineData("default/config", false)]
        [InlineData("..", false)]
        [InlineData("", false)]
        public void ThemeIsOneFolderName(string theme, bool expected) => Assert.Equal(expected, SiteBuilderContentPaths.IsFolderName(theme));

        [Theory]
        [InlineData("pages", "about-us.page", true)]
        [InlineData("blogs", "blogs/news.page", true)]
        [InlineData("themes", "default/config/settings_data.json", true)]
        [InlineData("pages", "about-us.json", false)]
        [InlineData("pages", "/about-us.page-draft", true)]
        [InlineData("blogs", "blogs/news.page-draft", true)]
        [InlineData("themes", "default/config/settings_data.json-draft", false)]
        [InlineData("pages", "about-us.html-draft", false)]
        [InlineData("themes", "default/layout.liquid", false)]
        [InlineData("assets", "x.page", false)]
        [InlineData("themes", "default/content/templates/product.json", false)]
        [InlineData("themes", "/default/config/settings_data.json", true)]
        [InlineData("themes", "default/config/builder_settings.json", false)]
        [InlineData("themes", "default/config/settings_schema.json", false)]
        [InlineData("themes", "default/config/template_rules.json", true)]
        [InlineData("themes", "default/config/brand.json", true)]
        [InlineData("themes", "default/config/palette.json", true)]
        [InlineData("themes", "default/config/palette.dark.json", true)]
        [InlineData("themes", "default/config/palette.light.json", false)]
        [InlineData("themes", "default/config/schemas/sections/banner.json", false)]
        [InlineData("themes", "default/Config/builder_settings.json", false)]
        [InlineData("themes", "default/config/schemas/settings_data.json", false)]
        [InlineData("themes", "settings_data.json", false)]
        [InlineData("themes", "default/snapshots/current.json", false)]
        [InlineData("themes", "default/snapshots/3/en-US.json", false)]
        [InlineData("themes", "default/catalogs/abc12345.json", false)]
        [InlineData("themes", "default/catalogs/current.json", false)]
        public async Task OnlyTheFilesTheDesignerWrites_AreWritable(string type, string path, bool expected)
        {
            var file = new SiteBuilderContentFile { ContentType = type, Path = path, Content = JToken.Parse("{\"settings\":{\"type\":\"settings\"},\"content\":[]}") };
            var request = new SiteBuilderContentRequest { StoreId = "s1", Files = [file] };

            Assert.Equal(expected, (await RequestValidators.Content("s1").ValidateAsync(request, TestContext.Current.CancellationToken)).IsValid);
        }
    }
}
