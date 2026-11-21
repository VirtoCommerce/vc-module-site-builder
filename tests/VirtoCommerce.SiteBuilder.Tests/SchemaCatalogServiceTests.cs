using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Data.Theme;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SchemaCatalogServiceTests
{
    private const string Catalog = """{"hash":"abcdefgh","sections":{},"blocks":{},"shared":{}}""";
    private const string CatalogFile = "themes/s1/default/catalogs/abcdefgh.json";
    private const string CutOff = """{"hash":"abcdefgh","sect""";

    private readonly FakeBlobStorage _storage = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetAsync_FileCutOffMidWrite_AnswersAsIfThereWereNone()
    {
        _storage.Files[CatalogFile] = CutOff;

        Assert.Null(await Service().GetAsync("s1", "abcdefgh", Ct));
    }

    [Fact]
    public async Task SaveAsync_FileCutOffMidWrite_WritesItAgain()
    {
        _storage.Files[CatalogFile] = CutOff;

        await Service().SaveAsync("s1", Catalog, Ct);

        Assert.Equal(Catalog, _storage.Files[CatalogFile]);
    }

    [Fact]
    public async Task SaveAsync_FileAlreadyReadable_DoesNotWriteItAgain()
    {
        _storage.Files[CatalogFile] = Catalog;

        await Service().SaveAsync("s1", Catalog, Ct);

        Assert.DoesNotContain(CatalogFile, _storage.Written);
    }

    [Fact]
    public async Task SaveAsync_SameHashTwice_KeepsFirstContentAndMakesItCurrent()
    {
        var catalogs = Service();
        await catalogs.SaveAsync("s1", """{ "hash": "abc12345", "sections": { "a": {} } }""", Ct);
        await catalogs.SaveAsync("s1", """{ "hash": "abc12345", "sections": { "b": {} } }""", Ct);

        Assert.NotNull(JObject.Parse(await catalogs.GetAsync("s1", "abc12345", Ct))["sections"]!["a"]);
        Assert.Equal(await catalogs.GetAsync("s1", "abc12345", Ct), await catalogs.GetAsync("s1", null, Ct));
    }

    [Fact]
    public async Task SaveAsync_HashThatIsAPath_ThrowsAndKeepsNothing()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().SaveAsync("s1", """{ "hash": "../x" }""", Ct));

        Assert.Empty(_storage.Written);
    }

    [Theory]
    [InlineData("""{ "hash": {} }""")]
    [InlineData("""{ "hash": ["abc12345"] }""")]
    [InlineData("""{ "hash": "abc12345", "sections": [] }""")]
    [InlineData("""{ "hash": "abc12345", "blocks": "banner" }""")]
    [InlineData("""{ "hash": "abc12345", "shared": { "_sections": [] } }""")]
    public async Task SaveAsync_UnreadableCatalog_ThrowsAndKeepsNothing(string catalog)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().SaveAsync("s1", catalog, Ct));

        Assert.Null(await Service().GetAsync("s1", null, Ct));
    }

    [Theory]
    [InlineData("""[]""", "is not an object")]
    [InlineData("""{ "tags": [] }""", "has tags that are not an object")]
    [InlineData("""{ "tags": { "button": {} } }""", "has the tag 'button', which does not start with vc- and lowercase words joined by '-'")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "onclick": { "type": "string" } } } } }""", "has the tag 'vc-x', which has the attribute 'onclick', which no kit tag takes")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "content": { "type": "html" } } } } }""",
        "has the tag 'vc-x', which has the attribute 'content', which has no type of string, number, boolean, select, date or link")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "size": { "type": "select", "options": [ { "label": "S" } ] } } } } }""",
        "has the tag 'vc-x', which has the attribute 'size', which has options that are not a list of values")]
    [InlineData("""{ "behaviors": { "each": { "options": {} } } }""", "has the behavior 'each', which is the name of a binding")]
    [InlineData("""{ "behaviors": { "count-up": { "value": { "type": "number", "min": "0" } } } }""",
        "has the behavior 'count-up', which has a value that has a min, a max or a description of another kind")]
    [InlineData("""{ "behaviors": { "tilt": { "options": { "max": { "type": "number", "script": "x" } } } } }""",
        "has the behavior 'tilt', which has the option 'max', which has a key a kit field does not have, 'script'")]
    public async Task SaveAsync_KitOfAnotherShape_ThrowsNamingItAndKeepsNothing(string kit, string error)
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => Service().SaveAsync("s1", $$"""{ "hash": "abc12345", "kit": {{kit}} }""", Ct));

        Assert.StartsWith($"The catalog's \"kit\" {error}.", exception.Message);
        Assert.Null(await Service().GetAsync("s1", null, Ct));
    }

    [Fact]
    public async Task SaveAsync_KitOfItsShape_IsKept()
    {
        const string kit = """
            {
              "tags": { "vc-button": { "description": "A button", "attributes": { "to": { "type": "link" }, "size": { "type": "select", "options": [ "sm", { "value": "lg", "label": "Large" } ] } } } },
              "behaviors": { "count-up": { "value": { "type": "number" }, "options": { "duration": { "type": "number", "min": 0, "max": 5000 } }, "writes": [ "--count" ] } }
            }
            """;

        await Service().SaveAsync("s1", $$"""{ "hash": "abc12345", "kit": {{kit}} }""", Ct);

        Assert.NotNull(JObject.Parse(await Service().GetAsync("s1", null, Ct))["kit"]!["tags"]!["vc-button"]);
    }

    private SchemaCatalogService Service() => new(new ThemeStorageFactory(_storage.StoreService, _storage, _storage, _storage));
}
