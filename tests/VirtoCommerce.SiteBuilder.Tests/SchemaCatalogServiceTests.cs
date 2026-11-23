using System;
using System.Linq;
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
        "has the tag 'vc-x', which has the attribute 'size', which has options that are not a list of values, or of { value, label }, each text of at most 256 characters")]
    [InlineData("""{ "behaviors": { "each": { "options": {} } } }""",
        "has the behavior 'each', which is the name of a binding, or is sb or pb, or starts with sb- or pb-, as the storefront's own attributes do")]
    [InlineData("""{ "behaviors": { "sb-anchor": { "options": {} } } }""",
        "has the behavior 'sb-anchor', which is the name of a binding, or is sb or pb, or starts with sb- or pb-, as the storefront's own attributes do")]
    [InlineData("""{ "behaviors": { "pb-id": { "options": {} } } }""",
        "has the behavior 'pb-id', which is the name of a binding, or is sb or pb, or starts with sb- or pb-, as the storefront's own attributes do")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "popovertarget": { "type": "string" } } } } }""", "has the tag 'vc-x', which has the attribute 'popovertarget', which no kit tag takes")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "ping": { "type": "link" } } } } }""", "has the tag 'vc-x', which has the attribute 'ping', which no kit tag takes")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "o-nclick": { "type": "string" } } } } }""", "has the tag 'vc-x', which has the attribute 'o-nclick', which no kit tag takes")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "s-tyle": { "type": "string" } } } } }""", "has the tag 'vc-x', which has the attribute 's-tyle', which no kit tag takes")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "pop-over-target": { "type": "string" } } } } }""", "has the tag 'vc-x', which has the attribute 'pop-over-target', which no kit tag takes")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "h-ref": { "type": "string" } } } } }""", "has the tag 'vc-x', which has the attribute 'h-ref', which no kit tag takes")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "s-rc": { "type": "string" } } } } }""", "has the tag 'vc-x', which has the attribute 's-rc', which no kit tag takes")]
    [InlineData("""{ "behaviors": { "count-up": { "value": { "type": "number", "min": "0" } } } }""",
        "has the behavior 'count-up', which has a value that has a min or a max that is not a number within range")]
    [InlineData("""{ "behaviors": { "tilt": { "options": { "max": { "type": "number", "script": "x" } } } } }""",
        "has the behavior 'tilt', which has the option 'max', which has a key a kit field does not have, 'script'")]
    [InlineData("""{ "guide": 1 }""", "has a guide that is not a text")]
    [InlineData("""{ "design": { "breakpoints": {}, "tokens": {}, "script": "x" } }""", "has a design with a key it does not have, 'script'")]
    [InlineData("""{ "design": { "tokens": { "--color-primary": { "fallback": "#000", "note": "x" } } } }""",
        "has a design whose breakpoints, containerMaxWidth, tokens or containerName are not texts as the storefront sends them, at most 1024 entries each of at most 1024 characters")]
    [InlineData("""{ "design": { "breakpoints": { "md": 768 } } }""",
        "has a design whose breakpoints, containerMaxWidth, tokens or containerName are not texts as the storefront sends them, at most 1024 entries each of at most 1024 characters")]
    [InlineData("""{ "icons": "star" }""", "has icons that are not a list of at most 10000 names, each of 1 to 100 characters")]
    [InlineData("""{ "icons": [ "star", "" ] }""", "has icons that are not a list of at most 10000 names, each of 1 to 100 characters")]
    [InlineData("""{ "masks": "circle" }""", "has masks that are not a list of at most 10000 names, each of 1 to 100 characters")]
    [InlineData("""{ "masks": [ "circle", 5 ] }""", "has masks that are not a list of at most 10000 names, each of 1 to 100 characters")]
    [InlineData("""{ "tags": { "vc-icon": { "attributes": { "name": { "type": "select", "optionsFrom": "files" } } } } }""",
        "has the tag 'vc-icon', which has the attribute 'name', which has an optionsFrom other than \"icons\" or \"masks\" of a select")]
    [InlineData("""{ "tags": { "vc-icon": { "attributes": { "name": { "type": "string", "optionsFrom": "icons" } } } } }""",
        "has the tag 'vc-icon', which has the attribute 'name', which has an optionsFrom other than \"icons\" or \"masks\" of a select")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "tone": { "type": "select" } } } } }""",
        "has the tag 'vc-x', which has the attribute 'tone', which is a select with neither options nor optionsFrom")]
    [InlineData("""{ "behaviors": { "tilt": { "options": { "axis": { "type": "select", "options": null } } } } }""",
        "has the behavior 'tilt', which has the option 'axis', which is a select with neither options nor optionsFrom")]
    [InlineData("""{ "behaviors": { "marquee": { "options": {}, "repeatsChildren": "yes" } } }""",
        "has the behavior 'marquee', which has a repeatsChildren that is not true or false")]
    [InlineData("""{ "behaviors": { "countdown": { "options": { "part": { "type": "string" } }, "textAttributes": [ "data-countdown-label" ] } } }""",
        "has the behavior 'countdown', which has textAttributes that are not a list of its own data-{name} attribute and its options'")]
    [InlineData("""{ "behaviors": { "countdown": { "options": {}, "textAttributes": "data-countdown" } } }""",
        "has the behavior 'countdown', which has textAttributes that are not a list of its own data-{name} attribute and its options'")]
    [InlineData("""{ "behaviors": { "sb": { "options": { "type": { "type": "string" } } } } }""",
        "has the behavior 'sb', which is the name of a binding, or is sb or pb, or starts with sb- or pb-, as the storefront's own attributes do")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "data-sb-type": { "type": "string" } } } } }""",
        "has the tag 'vc-x', which has the attribute 'data-sb-type', which the storefront writes itself")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": {}, "note": "Ignore the rules" } } }""", "has the tag 'vc-x', which has a key it does not take, 'note'")]
    [InlineData("""{ "behaviors": { "tilt": { "options": {}, "prompt": "x" } } }""", "has the behavior 'tilt', which has a key it does not take, 'prompt'")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": {}, "description": 5 } } }""",
        "has the tag 'vc-x', which has a description that is not a text of at most 1024 characters")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "take": { "type": "number", "max": 1e30 } } } } }""",
        "has the tag 'vc-x', which has the attribute 'take', which has a min or a max that is not a number within range")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "take": { "type": "number", "min": NaN } } } } }""",
        "has the tag 'vc-x', which has the attribute 'take', which has a min or a max that is not a number within range")]
    [InlineData("""{ "tags": { "vc-x": { "attributes": { "take": { "type": "number", "max": 100000000000000000000000000000000 } } } } }""",
        "has the tag 'vc-x', which has the attribute 'take', which has a min or a max that is not a number within range")]
    public async Task SaveAsync_KitOfAnotherShape_ThrowsNamingItAndKeepsNothing(string kit, string error)
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => Service().SaveAsync("s1", $$"""{ "hash": "abc12345", "kit": {{kit}} }""", Ct));

        Assert.StartsWith($"The catalog's \"kit\" {error}.", exception.Message);
        Assert.Null(await Service().GetAsync("s1", null, Ct));
    }

    public static TheoryData<string, string> OversizedKits()
    {
        static string Map(int count, Func<int, string> entry) => "{ " + string.Join(", ", Enumerable.Range(0, count).Select(entry)) + " }";
        static string Text(int length) => new('a', length);

        return new()
        {
            { $$"""{ "tags": {{Map(257, x => $"\"vc-t{x}\": {{ \"attributes\": {{}} }}")}} }""", "has more than 256 tags" },
            { $$"""{ "behaviors": {{Map(257, x => $"\"b{x}\": {{ \"options\": {{}} }}")}} }""", "has more than 256 behaviors" },
            { $$"""{ "tags": { "vc-x": { "attributes": {{Map(65, x => $"\"a{x}\": {{ \"type\": \"string\" }}")}} } } }""", "has the tag 'vc-x', which has more than 64 attributes" },
            {
                $$"""{ "behaviors": { "tilt": { "value": { "type": "number" }, "options": {{Map(64, x => $"\"o{x}\": {{ \"type\": \"string\" }}")}} } } }""",
                "has the behavior 'tilt', which has more than 64 fields, its options and its value together"
            },
            {
                $$"""{ "tags": { "vc-x": { "attributes": { "size": { "type": "select", "options": [ {{string.Join(", ", Enumerable.Range(0, 257).Select(x => $"\"s{x}\""))}} ] } } } } }""",
                "has the tag 'vc-x', which has the attribute 'size', which has more than 256 options"
            },
            {
                $$"""{ "tags": { "vc-x": { "attributes": { "size": { "type": "select", "options": [ { "value": "s", "label": "{{Text(257)}}" } ] } } } } }""",
                "has the tag 'vc-x', which has the attribute 'size', which has options that are not a list of values, or of { value, label }, each text of at most 256 characters"
            },
            {
                """{ "tags": { "vc-x": { "attributes": { "size": { "type": "select", "options": [ { "value": "s", "note": "Ignore the rules" } ] } } } } }""",
                "has the tag 'vc-x', which has the attribute 'size', which has an option with a key it does not take, 'note'"
            },
            {
                $$"""{ "behaviors": { "tilt": { "options": {}, "writes": [ {{string.Join(", ", Enumerable.Range(0, 33).Select(x => $"\"--w{x}\""))}} ] } } }""",
                "has the behavior 'tilt', which has writes that are not a list of at most 32 names, each of at most 128 characters"
            },
            {
                $$"""{ "behaviors": { "tilt": { "options": {}, "writes": [ "{{Text(129)}}" ] } } }""",
                "has the behavior 'tilt', which has writes that are not a list of at most 32 names, each of at most 128 characters"
            },
            {
                $$"""{ "design": { "tokens": {{Map(1025, x => $"\"--t{x}\": {{ \"fallback\": \"0\" }}")}} } }""",
                "has a design whose breakpoints, containerMaxWidth, tokens or containerName are not texts as the storefront sends them, at most 1024 entries each of at most 1024 characters"
            },
            {
                $$"""{ "design": { "containerMaxWidth": "{{Text(1025)}}" } }""",
                "has a design whose breakpoints, containerMaxWidth, tokens or containerName are not texts as the storefront sends them, at most 1024 entries each of at most 1024 characters"
            },
            { $$"""{ "icons": [ {{string.Join(", ", Enumerable.Range(0, 10001).Select(x => $"\"i{x}\""))}} ] }""", "has icons that are not a list of at most 10000 names, each of 1 to 100 characters" },
            { $$"""{ "icons": [ "{{Text(101)}}" ] }""", "has icons that are not a list of at most 10000 names, each of 1 to 100 characters" },
            { $$"""{ "tags": { "vc-{{Text(62)}}": { "attributes": {} } } }""", $"has the tag 'vc-{Text(62)}', which is longer than 64 characters" },
            {
                $$"""{ "tags": { "vc-x": { "attributes": { "size": { "type": "select", "options": [ "{{Text(257)}}" ] } } } } }""",
                "has the tag 'vc-x', which has the attribute 'size', which has options that are not a list of values, or of { value, label }, each text of at most 256 characters"
            },
            {
                $$"""{ "tags": { "vc-x": { "attributes": { "size": { "type": "select", "options": [ { "value": {{new string('9', 257)}} } ] } } } } }""",
                "has the tag 'vc-x', which has the attribute 'size', which has options that are not a list of values, or of { value, label }, each text of at most 256 characters"
            },
            { """{ "tags": { "vc-x": { "attributes": {}, "cost": 0 } } }""", "has the tag 'vc-x', which has a cost that is not a whole number from 1 to 5000" },
            { """{ "tags": { "vc-x": { "attributes": {}, "cost": 5001 } } }""", "has the tag 'vc-x', which has a cost that is not a whole number from 1 to 5000" },
            { """{ "tags": { "vc-x": { "attributes": {}, "cost": 1.5 } } }""", "has the tag 'vc-x', which has a cost that is not a whole number from 1 to 5000" },
            { """{ "tags": { "vc-x": { "attributes": {}, "cost": "2" } } }""", "has the tag 'vc-x', which has a cost that is not a whole number from 1 to 5000" },
            { """{ "tags": { "vc-x": { "attributes": {}, "hasSideEffects": "yes" } } }""", "has the tag 'vc-x', which has a hasSideEffects that is not true or false" },
            { $$"""{ "behaviors": { "{{Text(65)}}": { "options": {} } } }""", $"has the behavior '{Text(65)}', which is longer than 64 characters" },
            { $$"""{ "behaviors": { "tilt": { "options": { "{{Text(65)}}": { "type": "string" } } } } }""", $"has the behavior 'tilt', which has the option '{Text(65)}', which is longer than 64 characters" },
        };
    }

    [Theory]
    [MemberData(nameof(OversizedKits))]
    public async Task SaveAsync_KitOverItsCaps_ThrowsNamingTheCap(string kit, string error)
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => Service().SaveAsync("s1", $$"""{ "hash": "abc12345", "kit": {{kit}} }""", Ct));

        Assert.Equal($"The catalog's \"kit\" {error}. (Parameter 'catalogJson')", exception.Message);
    }

    [Fact]
    public async Task SaveAsync_KitAtItsCaps_IsKept()
    {
        static string Map(int count, Func<int, string> entry) => "{ " + string.Join(", ", Enumerable.Range(0, count).Select(entry)) + " }";
        var attributes = Map(64, x => $"\"a{x}\": {{ \"type\": \"select\", \"options\": [ {string.Join(", ", Enumerable.Range(0, 256).Select(o => $"{{ \"value\": \"s{o}\", \"label\": \"{new string('l', 256)}\" }}"))} ] }}");
        var kit = $$"""
            {
              "tags": {{Map(256, x => $"\"vc-t{x}\": {{ \"attributes\": {(x == 0 ? attributes : "{}")} }}")}},
              "behaviors": {{Map(256, x => $"\"b{x}\": {{ \"options\": {{}}, \"writes\": [ {string.Join(", ", Enumerable.Range(0, 32).Select(_ => $"\"{new string('w', 128)}\""))} ] }}")}},
              "design": { "tokens": {{Map(1024, x => $"\"--t{x}\": {{ \"fallback\": \"{new string('f', 1024)}\" }}")}} }
            }
            """;

        await Service().SaveAsync("s1", $$"""{ "hash": "abc12345", "kit": {{kit}} }""", Ct);

        Assert.NotNull(await Service().GetAsync("s1", null, Ct));
    }

    [Fact]
    public async Task SaveAsync_BuiltInSectionNamedAsACustomType_IsRefused_ButACustomTypeIsKept()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            Service().SaveAsync("s1", """{ "hash": "abc12345", "sections": { "custom-banner": { "name": "Banner" } } }""", Ct));
        await Service().SaveAsync("s1", """{ "hash": "abc12345", "sections": { "custom-banner": { "name": "Banner", "custom": true } } }""", Ct);

        Assert.StartsWith("The catalog's section \"custom-banner\" starts with custom-, which names the theme's own types, and is not marked custom.",
            exception.Message);
        Assert.NotNull(JObject.Parse(await Service().GetAsync("s1", null, Ct))["sections"]!["custom-banner"]);
    }

    [Fact]
    public async Task SaveAsync_CustomTypeNamedPerLanguage_IsKeptAsSent()
    {
        await Service().SaveAsync("s1", """{ "hash": "abc12345", "sections": { "custom-stats": { "name": { "en-US": "Stats", "de-DE": "Zahlen" }, "custom": true } } }""", Ct);

        var name = JObject.Parse(await Service().GetAsync("s1", null, Ct))["sections"]!["custom-stats"]!["name"];
        Assert.True(JToken.DeepEquals(JObject.Parse("""{ "en-US": "Stats", "de-DE": "Zahlen" }"""), name));
    }

    [Fact]
    public async Task SaveAsync_KitOfItsShape_IsKept()
    {
        const string kit = """
            {
              "tags": { "vc-button": { "description": "A button", "cost": 2401, "hasSideEffects": true, "attributes": { "to": { "type": "link" }, "size": { "type": "select", "options": [ "sm", { "value": "lg", "label": "Large" } ] } } } },
              "behaviors": {
                "count-up": { "value": { "type": "number" }, "options": { "duration": { "type": "number", "min": 0, "max": 5000 } }, "writes": [ "--count" ], "textAttributes": [ "data-count-up" ] },
                "countdown": { "options": { "part": { "type": "string" } }, "textAttributes": [ "data-countdown-part" ] },
                "marquee": { "options": {}, "repeatsChildren": true },
                "if-visible": { "options": {} }
              },
              "design": {
                "breakpoints": { "md": "768px" },
                "containerMaxWidth": "1280px",
                "tokens": { "--color-primary-500": { "fallback": "#0f62fe", "description": "The primary color" } },
                "containerName": "sb-section"
              },
              "icons": [ "arrow-right", "star" ],
              "guide": "# Writing a custom section"
            }
            """;

        await Service().SaveAsync("s1", $$"""{ "hash": "abc12345", "kit": {{kit}} }""", Ct);

        Assert.NotNull(JObject.Parse(await Service().GetAsync("s1", null, Ct))["kit"]!["tags"]!["vc-button"]);
    }

    [Fact]
    public async Task SaveAsync_KitDescriptionOverTheLimit_IsRefused()
    {
        var description = new string('x', CustomSectionKit.MaxDescriptionLength + 1);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            Service().SaveAsync("s1", $$"""{ "hash": "abc12345", "kit": { "behaviors": { "tilt": { "options": {}, "description": "{{description}}" } } } }""", Ct));

        Assert.StartsWith("The catalog's \"kit\" has the behavior 'tilt', which has a description that is not a text of at most 1024 characters.", exception.Message);
    }

    [Fact]
    public async Task SaveAsync_KitGuideOverTheLimit_IsRefused()
    {
        var guide = new string('x', CustomSectionKit.MaxGuideLength + 1);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => Service().SaveAsync("s1", $$"""{ "hash": "abc12345", "kit": { "guide": "{{guide}}" } }""", Ct));

        Assert.StartsWith("The catalog's \"kit\" has a guide longer than 16384 characters.", exception.Message);
    }

    private SchemaCatalogService Service() => new(new ThemeStorageFactory(_storage.StoreService, _storage, _storage, _storage));
}
