using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Data.Validation;

public class SiteBuilderContentGuard(ISiteBuilderHtmlSanitizer htmlSanitizer) : ThemeContentReader, ISiteBuilderContentGuard
{
    private const string HtmlFieldType = "html";
    private const string RichTextFieldType = "text";
    private const string MarkdownFieldType = "markdown";
    private const string UrlFieldType = "url";
    private const string LinkFieldType = "link";
    private const string ImagesFieldType = "images";
    private const string UrlFormat = "url";
    private const string MarkdownProperty = "markdown";
    private const string HtmlProperty = "html";
    private const string PaletteTokensKey = "paletteTokens";

    // keys whose values are URLs: link, url, href, src, image(s), and names ending in one of them (logoUrl, logo_image, permalink)
    private static readonly string[] UrlKeyEndings = ["url", "link", "href", "src", "image", "images"];

    public SiteBuilderContentGuard()
        : this(new SiteBuilderHtmlSanitizer())
    {
    }

    public virtual SiteBuilderContentGuardResult GuardPage(JToken document, string name, JObject catalog)
    {
        var result = AbstractTypeFactory<SiteBuilderContentGuardResult>.TryCreateInstance();
        if (document == null)
        {
            return result;
        }

        if (catalog != null)
        {
            GuardItems(GetItems(document), ModuleConstants.SchemaKinds.Sections, name, catalog, result);
        }

        GuardValues(document, name, result);

        return result;
    }

    public virtual SiteBuilderContentGuardResult GuardThemeFile(string file, JToken content, JObject catalog)
    {
        var result = AbstractTypeFactory<SiteBuilderContentGuardResult>.TryCreateInstance();
        if (content == null)
        {
            return result;
        }

        if (IsPaletteFile(file))
        {
            GuardPalette(content, file, catalog, result);
        }
        else if (catalog != null && file.StartsWith(ThemeStorage.TemplatesFolder + "/", StringComparison.OrdinalIgnoreCase))
        {
            GuardItems(GetItems(content), ModuleConstants.SchemaKinds.Sections, file, catalog, result);
        }
        else if (catalog != null && IsSectionGroupFile(file))
        {
            GuardItems((content as JObject)?["content"] as JArray ?? [], ModuleConstants.SchemaKinds.Sections, file, catalog, result);
        }

        GuardValues(content, file, result);

        return result;
    }

    /// <summary>The sections of a page document, in place: <c>content</c>, or all but the first element of the legacy array.</summary>
    protected virtual IEnumerable<JToken> GetItems(JToken document) => document switch
    {
        JObject envelope => envelope["content"] as JArray ?? [],
        JArray legacy => legacy.Skip(1),
        _ => [],
    };

    protected virtual void GuardItems(IEnumerable<JToken> items, string kind, string name, JObject catalog, SiteBuilderContentGuardResult result)
    {
        foreach (var item in items.OfType<JObject>().ToList())
        {
            var fields = GetFields(GetItemSchema(catalog, kind, GetString(item, "type")), kind, catalog);
            VisitValues(item, fields, path: null, (field, value, _) => GuardField(field, value, name, result));

            if (item["blocks"] is JArray blocks)
            {
                GuardItems(blocks, ModuleConstants.SchemaKinds.Blocks, name, catalog, result);
            }
        }
    }

    protected virtual void GuardField(JObject field, JToken value, string name, SiteBuilderContentGuardResult result)
    {
        if (value == null)
        {
            return;
        }

        if (IsLinkField(field) || GetString(field, "type") == ImagesFieldType)
        {
            foreach (var text in GetTexts(value))
            {
                var schemes = ContentUrlScheme.GetSchemes((string)text).ToList();
                if (schemes.FirstOrDefault(ContentUrlScheme.RunsScript) is { } scheme)
                {
                    AddScriptUrlError(text, scheme, name, result);
                }
                // a link may not open a document it carries; an image may
                else if (IsLinkField(field) && schemes.Contains(ContentUrlScheme.Data))
                {
                    result.Errors.Add($"{name}: '{GetPath(text)}' links to a data: URL");
                }
            }
        }

        var type = GetString(field, "type");
        var resultType = GetString(field, "resultType");
        if (type is HtmlFieldType or RichTextFieldType || (type == MarkdownFieldType && resultType == HtmlFieldType))
        {
            // every text of the value is HTML: a string, the texts of a translation, the html of a markdown value
            foreach (var text in GetTexts(value).Where(x => x.Parent is not JProperty { Name: MarkdownProperty }))
            {
                Sanitize(text, result);
            }
        }
        else if (type == MarkdownFieldType && resultType != MarkdownFieldType && value is JObject translation && !IsMarkdownValue(translation))
        {
            // the texts of a translated markdown value are its HTML, as the designer stores them
            foreach (var text in translation.Properties().Select(x => x.Value).OfType<JValue>().Where(x => x.Type == JTokenType.String))
            {
                Sanitize(text, result);
            }
        }
    }

    /// <summary>Checks that need no schema: the values of keys that name a URL for a script URL, and the HTML of every markdown value.</summary>
    protected virtual void GuardValues(JToken content, string name, SiteBuilderContentGuardResult result)
    {
        foreach (var token in GetTokens(content))
        {
            if (token is JObject markdown && IsMarkdownValue(markdown))
            {
                Sanitize((JValue)markdown[HtmlProperty], result);
            }
            else if (token is JProperty property && IsUrlKey(property.Name))
            {
                foreach (var text in GetUrlTexts(property.Value))
                {
                    if (ContentUrlScheme.GetSchemes((string)text).FirstOrDefault(ContentUrlScheme.RunsScript) is { } scheme)
                    {
                        AddScriptUrlError(text, scheme, name, result);
                    }
                }
            }
        }
    }

    /// <summary>Only URLs are checked: a text such as "JavaScript: tips" is no link.</summary>
    protected virtual bool IsUrlKey(string key) =>
        UrlKeyEndings.Any(x => key.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>The URL itself, a list of URLs, or the texts of a translation; nested objects are checked by their own keys.</summary>
    protected virtual IEnumerable<JValue> GetUrlTexts(JToken value) => value switch
    {
        JValue { Type: JTokenType.String } text => [text],
        JArray list => list.OfType<JValue>().Where(x => x.Type == JTokenType.String),
        JObject translation when !IsMarkdownValue(translation) => translation.Properties().Select(x => x.Value).OfType<JValue>().Where(x => x.Type == JTokenType.String),
        _ => [],
    };

    // a value can be both a schema field and of a key that names a URL
    private static void AddScriptUrlError(JValue text, string scheme, string name, SiteBuilderContentGuardResult result)
    {
        var error = $"{name}: '{GetPath(text)}' holds a {scheme}: URL, which runs script";
        if (!result.Errors.Contains(error))
        {
            result.Errors.Add(error);
        }
    }

    /// <summary>Token names the storefront announced in the catalog, else of its shape; values that are CSS colors.</summary>
    protected virtual void GuardPalette(JToken content, string file, JObject catalog, SiteBuilderContentGuardResult result)
    {
        if (content.Type == JTokenType.Null)
        {
            return;
        }

        if (content is not JObject palette)
        {
            result.Errors.Add($"{file}: not an object of token colors");
            return;
        }

        var tokens = GetPaletteTokens(catalog);
        foreach (var property in palette.Properties())
        {
            if (tokens != null ? !tokens.Contains(property.Name) : !ThemePaletteValues.IsTokenName(property.Name))
            {
                result.Errors.Add($"{file}: '{property.Name}' is not a palette token the storefront has");
            }

            if (property.Value.Type != JTokenType.Null && !(property.Value is JValue { Type: JTokenType.String } color && ThemePaletteValues.IsColor((string)color)))
            {
                result.Errors.Add($"{file}: '{property.Name}' is not a CSS color");
            }
        }
    }

    // light and dark together: a palette written for an older storefront may hold a token of either
    protected virtual ISet<string> GetPaletteTokens(JObject catalog)
    {
        if (catalog?[PaletteTokensKey] is not JObject tokens)
        {
            return null;
        }

        var names = GetStrings(tokens["light"]).Concat(GetStrings(tokens["dark"])).ToHashSet(StringComparer.Ordinal);
        return names.Count > 0 ? names : null;
    }

    protected virtual void Sanitize(JValue text, SiteBuilderContentGuardResult result)
    {
        if (text is not { Type: JTokenType.String })
        {
            return;
        }

        var html = (string)text;
        var sanitized = htmlSanitizer.Sanitize(html);
        if (!string.Equals(html, sanitized, StringComparison.Ordinal))
        {
            text.Value = sanitized;
            result.Changed = true;
        }
    }

    protected virtual bool IsLinkField(JObject field) =>
        GetString(field, "format") == UrlFormat || GetString(field, "type") is UrlFieldType or LinkFieldType;

    /// <summary>What a markdown setting stores when its schema names no result type: <c>{ markdown, html }</c>.</summary>
    protected static bool IsMarkdownValue(JObject value) =>
        value.ContainsKey(MarkdownProperty) && value[HtmlProperty] is JValue { Type: JTokenType.String };

    private static bool IsPaletteFile(string file) =>
        string.Equals(file, ThemeStorage.PaletteFile, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(file, ThemeStorage.DarkPaletteFile, StringComparison.OrdinalIgnoreCase);

    private static bool IsSectionGroupFile(string file) =>
        string.Equals(file, ThemeStorage.HeaderGroupFile, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(file, ThemeStorage.FooterGroupFile, StringComparison.OrdinalIgnoreCase);

    private static List<JValue> GetTexts(JToken value) =>
        GetTokens(value).OfType<JValue>().Where(x => x.Type == JTokenType.String).ToList();

    private static List<JToken> GetTokens(JToken value) => value is JContainer container ? container.DescendantsAndSelf().ToList() : [value];

    private static string GetPath(JToken token) => string.IsNullOrEmpty(token.Path) ? "$" : token.Path;
}
