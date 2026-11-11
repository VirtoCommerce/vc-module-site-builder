using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.StoreModule.Core.Model;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests
{
    public class SiteBuilderContentRequestValidatorTests
    {
        private const string Store = "vccom";

        [Fact]
        public async Task AReadInsideTheStore_IsValid()
        {
            var result = await Validator().ValidateAsync(Request(paths: ["/about-us.page"]), TestContext.Current.CancellationToken);

            Assert.True(result.IsValid);
        }

        [Fact]
        public async Task ARequestWithoutAContentType_IsInvalid()
        {
            var result = await Validator().ValidateAsync(Request(type: null), TestContext.Current.CancellationToken);

            Assert.False(result.IsValid);
        }

        [Fact]
        public async Task ASaveNamesTheContentTypePerFile()
        {
            var request = Request(type: null, files: [File("pages", "about-us.page"), File("themes", "default/config/settings_data.json")]);

            var result = await Validator().ValidateAsync(request, TestContext.Current.CancellationToken);

            Assert.True(result.IsValid);
        }

        [Fact]
        public async Task ASaveOfAFileTheDesignerDoesNotWrite_IsInvalid()
        {
            var request = Request(type: null, files: [File("pages", "about-us.page"), File("themes", "default/config/builder_settings.json")]);

            var result = await Validator().ValidateAsync(request, TestContext.Current.CancellationToken);

            Assert.False(result.IsValid);
            Assert.Contains("builder_settings.json", result.Errors.Single().ErrorMessage);
        }

        [Fact]
        public async Task ASaveWithAMissingFile_IsInvalid()
        {
            var result = await Validator().ValidateAsync(Request(type: null, files: [null]), TestContext.Current.CancellationToken);

            Assert.False(result.IsValid);
        }

        [Fact]
        public async Task ATheme_IsOneFolderName()
        {
            var result = await Validator().ValidateAsync(Request(theme: "../other-store"), TestContext.Current.CancellationToken);

            Assert.False(result.IsValid);
        }

        [Fact]
        public async Task AnUnknownStore_IsInvalid()
        {
            var result = await Validator().ValidateAsync(Request(storeId: "other-store"), TestContext.Current.CancellationToken);

            Assert.False(result.IsValid);
        }

        [Theory]
        [InlineData("../about-us.page", "must stay inside")]
        [InlineData("/a//b.page", "must stay inside")]
        [InlineData("/news./a.page", "Windows")]
        [InlineData("a\\b.page", "contains")]
        public async Task EachPathRule_SaysWhatIsWrong(string path, string reason)
        {
            var result = await Validator().ValidateAsync(Request(paths: [path]), TestContext.Current.CancellationToken);

            Assert.Contains(reason, result.Errors.Single().ErrorMessage);
        }

        [Theory]
        [InlineData("default/layout.liquid", "theme JSON file")]
        [InlineData("settings_data.json", "theme folder")]
        [InlineData("default/config/builder_settings.json", "config folder")]
        public async Task EachThemeFileRule_SaysWhatIsWrong(string path, string reason)
        {
            var result = await Validator().ValidateAsync(Request(type: null, files: [File("themes", path)]), TestContext.Current.CancellationToken);

            Assert.Contains(reason, result.Errors.Single().ErrorMessage);
        }

        [Fact]
        public async Task APageSaved_IsAPageDocument()
        {
            var request = Request(type: null, files: [File("pages", "about-us.page", """{"settings":{"type":"settings"},"content":"x"}""")]);

            var result = await Validator().ValidateAsync(request, TestContext.Current.CancellationToken);

            Assert.Contains("about-us.page: Missing content array", result.Errors.Single().ErrorMessage);
        }

        [Fact]
        public async Task AMalformedRequest_DoesNotLookUpTheStore()
        {
            var storeService = new FakeStoreService(new Store { Id = Store });

            await new SiteBuilderContentRequestValidator(new SiteBuilderStoreRequestValidator(storeService), new SiteBuilderContentFileValidator()).ValidateAsync(Request(paths: ["../appsettings.json"]), TestContext.Current.CancellationToken);

            Assert.Null(storeService.LastRequestedId);
        }

        private static SiteBuilderContentRequestValidator Validator() =>
            new(new SiteBuilderStoreRequestValidator(new FakeStoreService(new Store { Id = Store })), new SiteBuilderContentFileValidator());

        private static SiteBuilderContentRequest Request(string storeId = Store, string type = "pages", string theme = null,
            string[] paths = null, SiteBuilderContentFile[] files = null) =>
            new()
            {
                StoreId = storeId,
                ContentType = type,
                Theme = theme,
                Paths = paths ?? [],
                Files = files,
            };

        private static SiteBuilderContentFile File(string type, string path, string content = """{"settings":{"type":"settings"},"content":[]}""") =>
            new() { ContentType = type, Path = path, Content = JToken.Parse(content) };
    }
}
