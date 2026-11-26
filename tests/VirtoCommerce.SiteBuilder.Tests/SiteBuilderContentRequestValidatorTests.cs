using System.Linq;
using System.Threading.Tasks;
using FluentValidation;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Platform.Core.DynamicProperties;
using VirtoCommerce.SiteBuilder.Core;
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

            await RequestValidators.Content(storeService).ValidateAsync(Request(paths: ["../appsettings.json"]), TestContext.Current.CancellationToken);

            Assert.Null(storeService.LastRequestedId);
        }

        [Theory]
        [InlineData("coffee/sections/header-group.json", true)]
        [InlineData("default/sections/header-group.json", false)]
        [InlineData("sections/header-group.json", false)]
        public async Task AThemeFile_IsInTheStoresThemeFolder(string path, bool valid)
        {
            var store = new Store
            {
                Id = Store,
                DynamicProperties = [new DynamicObjectProperty { Name = ModuleConstants.Theme.ThemeNamePropertyName, Values = [new DynamicPropertyObjectValue { Value = "coffee" }] }],
            };

            var result = await RequestValidators.Content(new FakeStoreService(store))
                .ValidateAsync(Request(type: null, files: [File("themes", path, """{ "content": [] }""")]), TestContext.Current.CancellationToken);

            Assert.Equal(valid, result.IsValid);
            Assert.All(result.Errors, x => Assert.Contains($"\"{path}\" is not in the store's theme folder \"coffee\"", x.ErrorMessage));
        }

        [Theory]
        [InlineData("default/agent-test.json")]
        [InlineData("default/sections/x.json")]
        public async Task AThemeFileTheBuilderDoesNotRead_IsInvalidNamingIt(string path)
        {
            var result = await Validator().ValidateAsync(Request(type: null, files: [File("themes", path, "{}")]), TestContext.Current.CancellationToken);

            Assert.Contains($"\"{path}\" is not a theme file the builder saves", result.Errors.Single().ErrorMessage);
        }

        [Fact]
        public async Task AFileTheBuilderSavesNamedWithoutTheThemeFolder_InAThemeNamedAsOneOfItsFolders_IsInvalidNamingIt()
        {
            const string path = "sections/header-group.json";
            var store = new Store
            {
                Id = Store,
                DynamicProperties = [new DynamicObjectProperty { Name = ModuleConstants.Theme.ThemeNamePropertyName, Values = [new DynamicPropertyObjectValue { Value = "sections" }] }],
            };

            var result = await RequestValidators.Content(new FakeStoreService(store))
                .ValidateAsync(Request(type: null, files: [File("themes", path, """{ "content": [] }""")]), TestContext.Current.CancellationToken);

            Assert.Contains($"\"{path}\" is not a theme file the builder saves", result.Errors.Single().ErrorMessage);
        }

        [Theory]
        [InlineData("default/sections/header-group.json", true)]
        [InlineData("coffee/sections/header-group.json", false)]
        public async Task AThemeFile_OfAStoreWithoutATheme_IsInTheDefaultThemeFolder(string path, bool valid)
        {
            var result = await Validator().ValidateAsync(Request(type: null, files: [File("themes", path, """{ "content": [] }""")]), TestContext.Current.CancellationToken);

            Assert.Equal(valid, result.IsValid);
            Assert.All(result.Errors, x => Assert.Contains($"\"{path}\" is not in the store's theme folder \"default\"", x.ErrorMessage));
        }

        private static IValidator<SiteBuilderContentRequest> Validator() =>
            RequestValidators.Content(new FakeStoreService(new Store { Id = Store }));

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
