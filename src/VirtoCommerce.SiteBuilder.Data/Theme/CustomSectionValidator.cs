using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom.Events;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.AssetsModule.Core.Assets;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>The rules of <c>docs/contracts/custom-sections.md</c>: what the storefront would leave out of a type is refused here, with the rule it breaks.</summary>
public class CustomSectionValidator(IBlobUrlResolver blobUrlResolver, ILogger<CustomSectionValidator> logger) : ICustomSectionValidator
{
    private const int MaxPartSize = 64 * 1024;
    private const int MaxTotalSize = 256 * 1024;
    private const int MaxParseErrors = 3;
    private const int MaxSamples = 3;
    private const int MaxQuoteLength = 60;
    private const int MaxCssDepth = 32;
    private const int MaxTemplateDepth = 32;

    // the HTML parser takes seconds over thousands of nested elements, so a template far deeper than the limit is not parsed at all
    private const int MaxScannedTemplateDepth = 256;
    private const int MaxAtRuleDepth = 4;
    private const string KeyframesAtRule = "keyframes";

    // what follows the type in a keyframes name, which is not scoped: custom-hero__fade
    private const string KeyframesSeparator = "__";
    private const string AllowedAtRulesText = "@media, @supports, @container, @starting-style and @keyframes";

    // no doctype, as the storefront's sanitizer parses: a document without one is read in quirks mode, where a table may stay in a p
    private const string DocumentStart = "<html><head></head><body>";
    private const string AssetProbe = "file";

    // the CSS parser writes 'transparent' so

    private const string FormatKey = "format";
    private const string NameKey = "name";
    private const string SchemaKey = "schema";
    private const string TemplateKey = "template";
    private const string CssKey = "css";
    private const string SettingsKey = "settings";

    private const string StringFieldType = "string";
    private const string TextFieldType = "text";
    private const string ImagesFieldType = "images";
    private const string SelectFieldType = "select";
    private const string ListFieldType = "list";
    private const string ObjectFieldType = "object";
    private const string UrlFormat = "url";

    private const string EachAttribute = "data-each";
    private const string IfAttribute = "data-if";
    private const string HtmlAttribute = "data-html";
    private const string AltAttribute = "alt";
    private const string LabelAttribute = "label";
    private const string DataPrefix = "data-";

    // a rel that gives the page a link opens a hold on the storefront's window
    private const string OpenerRel = "opener";

    private const string KitKey = "kit";

    private static readonly HashSet<string> TypeKeys = new(StringComparer.Ordinal) { FormatKey, NameKey, SchemaKey, TemplateKey, CssKey };

    private static readonly HashSet<string> SchemaKeys = new(StringComparer.Ordinal) { SettingsKey, "targetTemplates", "limit", "icon" };

    // no 'visibility' and no 'request': the designer runs the one and sends the other
    private static readonly HashSet<string> FieldKeys = new(StringComparer.Ordinal)
    {
        "id", "type", "label", "default", "translatable", "tab", "info", "placeholder", "required", "multiline", "multiple", "format", "options",
        "element", "min", "max", "step",
    };

    // the fields the designer shows as text to read, which hold no value
    private static readonly HashSet<string> LabelFieldTypes = new(StringComparer.Ordinal) { "header", "paragraph" };

    private static readonly HashSet<string> ValueFieldTypes = new(StringComparer.Ordinal)
    {
        StringFieldType, TextFieldType, "number", "slider", "checkbox", SelectFieldType, "color-token", ImagesFieldType, "calendar",
    };

    private static readonly HashSet<string> FormControls = new(StringComparer.Ordinal)
    {
        "form", "input", "select", "textarea", "button", "option", "optgroup", "datalist", "fieldset", "legend", "label", "output",
    };

    private static readonly HashSet<string> AllowedElements =
        SiteBuilderHtmlSanitizer.AllowedTags.Where(x => !FormControls.Contains(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.Ordinal)
    {
        "class", "role", "alt", "title", "lang", "dir", "hidden", "href", "target", "rel", "src", "srcset", "sizes", "loading", "decoding",
        "width", "height", "datetime", "colspan", "rowspan", "scope",
    };

    // what an element takes besides the attributes every element does
    private static readonly Dictionary<string, HashSet<string>> ElementAttributes = new(StringComparer.Ordinal)
    {
        ["video"] = new(StringComparer.Ordinal) { "autoplay", "muted", "loop", "playsinline", "controls", "preload", "poster" },
        ["source"] = new(StringComparer.Ordinal) { "type", "media" },
        ["details"] = new(StringComparer.Ordinal) { "open" },
        ["blockquote"] = new(StringComparer.Ordinal) { "cite" },
        ["q"] = new(StringComparer.Ordinal) { "cite" },
    };

    // people read what these hold, as they read the template's text
    private static readonly HashSet<string> TextAttributes = new(StringComparer.Ordinal) { "alt", "title", "aria-label", "label" };

    private static readonly string[] LinkSchemes = ["https:", "mailto:", "tel:"];

    private static readonly string[] LinkTargets = ["_self", "_blank"];

    // what follows the type in a class of its own: custom-hero-dark, custom-hero__title
    private static readonly string[] ClassSeparators = ["-", "__"];

    private static readonly char[] Whitespace = [' ', '\t', '\n', '\r', '\f'];

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private static readonly HashSet<string> AllowedAtRules = new(StringComparer.OrdinalIgnoreCase) { "media", "supports", "container", "starting-style", KeyframesAtRule };

    // the functions that load an image from a URL given as a string
    private static readonly HashSet<string> ImageFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "url", "src", "image", "image-set", "-webkit-image-set", "cross-fade", "-webkit-cross-fade",
    };

    // the functions whose value is known only when the page is shown, with a custom --name() function
    private static readonly HashSet<string> LateValueFunctions = new(StringComparer.OrdinalIgnoreCase) { "var", "attr", "env", "if" };

    private static readonly HashSet<string> ScriptProperties = new(StringComparer.OrdinalIgnoreCase) { "behavior", "-moz-binding" };

    private static readonly HashSet<string> ColorFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "rgb", "rgba", "hsl", "hsla", "hwb", "lab", "lch", "oklab", "oklch", "color",
    };

    // CSS Color 4's named colors, without transparent and currentcolor, which are not literal colors of a palette
    private static readonly HashSet<string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "aliceblue", "antiquewhite", "aqua", "aquamarine", "azure", "beige", "bisque", "black", "blanchedalmond", "blue", "blueviolet", "brown",
        "burlywood", "cadetblue", "chartreuse", "chocolate", "coral", "cornflowerblue", "cornsilk", "crimson", "cyan", "darkblue", "darkcyan",
        "darkgoldenrod", "darkgray", "darkgreen", "darkgrey", "darkkhaki", "darkmagenta", "darkolivegreen", "darkorange", "darkorchid", "darkred",
        "darksalmon", "darkseagreen", "darkslateblue", "darkslategray", "darkslategrey", "darkturquoise", "darkviolet", "deeppink", "deepskyblue",
        "dimgray", "dimgrey", "dodgerblue", "firebrick", "floralwhite", "forestgreen", "fuchsia", "gainsboro", "ghostwhite", "gold", "goldenrod",
        "gray", "green", "greenyellow", "grey", "honeydew", "hotpink", "indianred", "indigo", "ivory", "khaki", "lavender", "lavenderblush",
        "lawngreen", "lemonchiffon", "lightblue", "lightcoral", "lightcyan", "lightgoldenrodyellow", "lightgray", "lightgreen", "lightgrey",
        "lightpink", "lightsalmon", "lightseagreen", "lightskyblue", "lightslategray", "lightslategrey", "lightsteelblue", "lightyellow", "lime",
        "limegreen", "linen", "magenta", "maroon", "mediumaquamarine", "mediumblue", "mediumorchid", "mediumpurple", "mediumseagreen",
        "mediumslateblue", "mediumspringgreen", "mediumturquoise", "mediumvioletred", "midnightblue", "mintcream", "mistyrose", "moccasin",
        "navajowhite", "navy", "oldlace", "olive", "olivedrab", "orange", "orangered", "orchid", "palegoldenrod", "palegreen", "paleturquoise",
        "palevioletred", "papayawhip", "peachpuff", "peru", "pink", "plum", "powderblue", "purple", "rebeccapurple", "red", "rosybrown",
        "royalblue", "saddlebrown", "salmon", "sandybrown", "seagreen", "seashell", "sienna", "silver", "skyblue", "slateblue", "slategray",
        "slategrey", "snow", "springgreen", "steelblue", "tan", "teal", "thistle", "tomato", "turquoise", "violet", "wheat", "white", "whitesmoke",
        "yellow", "yellowgreen",
    };

    private static readonly HashSet<string> MotionProperties = new(StringComparer.OrdinalIgnoreCase) { "animation", "animation-name", "transition" };

    private static readonly HashSet<string> ReducedMotionValues = new(StringComparer.OrdinalIgnoreCase) { "reduce", "no-preference" };

    // the logical property for each physical one, which follows the page's direction
    private static readonly Dictionary<string, string> LogicalProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["margin-left"] = "margin-inline-start",
        ["margin-right"] = "margin-inline-end",
        ["padding-left"] = "padding-inline-start",
        ["padding-right"] = "padding-inline-end",
        ["left"] = "inset-inline-start",
        ["right"] = "inset-inline-end",
        ["border-left"] = "border-inline-start",
        ["border-right"] = "border-inline-end",
        ["border-left-width"] = "border-inline-start-width",
        ["border-right-width"] = "border-inline-end-width",
        ["border-left-style"] = "border-inline-start-style",
        ["border-right-style"] = "border-inline-end-style",
        ["border-left-color"] = "border-inline-start-color",
        ["border-right-color"] = "border-inline-end-color",
        ["border-top-left-radius"] = "border-start-start-radius",
        ["border-top-right-radius"] = "border-start-end-radius",
        ["border-bottom-left-radius"] = "border-end-start-radius",
        ["border-bottom-right-radius"] = "border-end-end-radius",
        ["scroll-margin-left"] = "scroll-margin-inline-start",
        ["scroll-margin-right"] = "scroll-margin-inline-end",
        ["scroll-padding-left"] = "scroll-padding-inline-start",
        ["scroll-padding-right"] = "scroll-padding-inline-end",
    };

    // the logical values for left and right, by the property that takes them
    private static readonly Dictionary<string, (string Left, string Right)> LogicalValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text-align"] = ("start", "end"),
        ["float"] = ("inline-start", "inline-end"),
        ["clear"] = ("inline-start", "inline-end"),
    };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex SettingIdPattern = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.None, RegexTimeout);
    // the path is trimmed after the match: a pattern that trims it backtracks over a long run of spaces
    private static readonly Regex BindingPattern = new(@"\{\{([^{}]*)\}\}", RegexOptions.None, RegexTimeout);
    private static readonly Regex SingleBindingPattern = new(@"^\s*\{\{([^{}]*)\}\}\s*$", RegexOptions.None, RegexTimeout);
    private static readonly Regex PathPattern = new(@"^(settings|item)\.([A-Za-z][A-Za-z0-9_]*)(?:\.([A-Za-z][A-Za-z0-9_]*))?$", RegexOptions.None, RegexTimeout);
    private static readonly Regex IsoDatePattern = new(@"^\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:\d{2})?)?$", RegexOptions.None, RegexTimeout);

    // an escaped dot, slash or backslash, which a URL resolves as the character
    private static readonly Regex EscapedPathPattern = new("%(?:2[eEfF]|5[cC])", RegexOptions.None, RegexTimeout);
    private static readonly Regex AnchorPattern = new("^#[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.None, RegexTimeout);
    // the properties whose value may hold a color by its name; anywhere else a name such as tan may be anything
    private static readonly Regex ColorPropertyPattern = new("color|^background|^border|^outline|^column-rule|^text-decoration|shadow$|^fill$|^stroke$|^filter$",
        RegexOptions.None, RegexTimeout);
    private static readonly Regex ImageDataUrlPattern = new("^data:image/[a-z0-9.+-]+[;,]", RegexOptions.IgnoreCase, RegexTimeout);

    public CustomSectionValidator()
        : this(blobUrlResolver: null)
    {
    }

    public CustomSectionValidator(IBlobUrlResolver blobUrlResolver)
        : this(blobUrlResolver, NullLogger<CustomSectionValidator>.Instance)
    {
    }

    public virtual CustomSectionValidationResult Validate(string type, JToken content, JObject catalog)
    {
        var result = AbstractTypeFactory<CustomSectionValidationResult>.TryCreateInstance();
        var check = new CustomSectionCheck(type, result) { Kit = ReadKit(catalog) };
        if (!ThemeStorage.IsCustomSectionType(type))
        {
            check.AddError($"'{type}' is not a custom section type, which is 'custom-' and lowercase words joined by '-', at most 64 characters");
            return result;
        }

        if (content is not JObject file)
        {
            check.AddError("not an object");
            return result;
        }

        ValidateShape(check, file);

        // every pattern has a timeout, which refuses the type rather than fails the request
        try
        {
            if (GetPart(check, file, TemplateKey) is { } template && check.Settings != null)
            {
                ValidateTemplate(check, template);
            }

            if (GetPart(check, file, CssKey) is { } css)
            {
                ValidateCss(check, css);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            check.AddError("the type takes too long to check: write its template and its CSS plainer");
        }
        // a fault of the check refuses the type it could not check, rather than the request
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The custom section type {Type} could not be checked.", type);
            check.AddError("the type could not be checked, so it is not saved: the platform's log has the cause");
        }

        AddWarnings(check);

        return result;
    }

    public virtual string GetSizeError(IDictionary<string, JToken> types)
    {
        var size = types.Values.Where(x => x != null).Sum(x => (long)Encoding.UTF8.GetByteCount(x.ToString(Formatting.None)));
        if (size <= MaxTotalSize)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{ThemeStorage.CustomSectionsFolder}: the custom section types are {FormatSize(size)} of JSON together, more than {FormatSize(MaxTotalSize)}: the snapshot carries every type to every page");
    }

    public virtual CustomSectionRules GetRules()
    {
        var result = AbstractTypeFactory<CustomSectionRules>.TryCreateInstance();
        result.Elements = AllowedElements.Order(StringComparer.Ordinal).ToList();
        result.Attributes = AllowedAttributes.Order(StringComparer.Ordinal).ToList();
        result.BindingAttributes = [EachAttribute, IfAttribute, HtmlAttribute];
        result.TextAttributes = TextAttributes.Order(StringComparer.Ordinal).ToList();
        result.LinkSchemes = [.. LinkSchemes];
        result.LinkTargets = [.. LinkTargets];
        result.RefusedRels = [OpenerRel];
        result.ClassSeparators = [.. ClassSeparators];
        result.SchemaKeys = [.. SchemaKeys];
        result.FieldTypes = [.. LabelFieldTypes, .. ValueFieldTypes, ListFieldType, ObjectFieldType];
        result.FieldKeys = [.. FieldKeys];
        result.AtRules = [.. AllowedAtRules];
        result.KeyframesSeparator = KeyframesSeparator;
        result.ElementAttributes = ElementAttributes.ToDictionary(x => x.Key, x => (IList<string>)[.. x.Value], StringComparer.Ordinal);
        result.ReservedDataPrefixes = [.. CustomSectionKit.ReservedBehaviorPrefixes.Select(x => DataPrefix + x)];
        result.ScriptProperties = ScriptProperties.Order(StringComparer.Ordinal).ToList();
        result.ImageFunctions = [.. ImageFunctions];
        result.LateValueFunctions = [.. LateValueFunctions];
        result.LogicalProperties = LogicalProperties
            .Concat(LogicalValues.SelectMany(x => new[]
            {
                KeyValuePair.Create($"{x.Key}: left", $"{x.Key}: {x.Value.Left}"),
                KeyValuePair.Create($"{x.Key}: right", $"{x.Key}: {x.Value.Right}"),
            }))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        result.KitFieldTypes = [.. CustomSectionKit.FieldTypes];
        result.KitDeniedAttributes = [.. CustomSectionKit.DeniedAttributePrefixes.Select(x => x + "*"), .. CustomSectionKit.DeniedAttributes];
        result.KitLinkAttributes = [.. CustomSectionKit.LinkAttributes];
        result.MaxTemplateDepth = MaxTemplateDepth;
        result.MaxCssDepth = MaxCssDepth;
        result.MaxAtRuleDepth = MaxAtRuleDepth;
        result.MaxPartSize = MaxPartSize;
        result.MaxTotalSize = MaxTotalSize;

        return result;
    }

    protected virtual void ValidateShape(CustomSectionCheck check, JObject file)
    {
        foreach (var key in file.Properties().Select(x => x.Name).Where(x => !TypeKeys.Contains(x)))
        {
            check.AddError($"'{key}' is not a key of a custom section type, which has format, name, schema, template and css");
        }

        if (file[FormatKey] is not JValue { Type: JTokenType.Integer } format || format.Value<long>() != 1)
        {
            check.AddError("'format' is not 1");
        }

        if (!IsName(file[NameKey]))
        {
            check.AddError("'name' is not a text, or a text per language");
        }

        if (file[SchemaKey] is JObject schema)
        {
            ValidateSchema(check, schema);
        }
        else
        {
            check.AddError("'schema' is not an object with the list of 'settings'");
        }
    }

    protected virtual void ValidateSchema(CustomSectionCheck check, JObject schema)
    {
        foreach (var key in schema.Properties().Select(x => x.Name).Where(x => !SchemaKeys.Contains(x)))
        {
            check.AddError($"'schema.{key}' is not a key of a custom section's schema, which has settings, targetTemplates, limit and icon");
        }

        if (schema[SettingsKey] is JArray settings)
        {
            check.Settings = ReadSettings(check, settings, parent: null);
        }
        else
        {
            check.AddError("'schema.settings' is not a list of settings");
        }

        if (schema["targetTemplates"] is { } targets && !(targets is JArray list && list.All(x => x is JValue { Type: JTokenType.String } key && !string.IsNullOrWhiteSpace((string)key))))
        {
            check.AddError("'schema.targetTemplates' is not a list of template names");
        }

        if (schema["limit"] is { } limit && !(limit is JValue { Type: JTokenType.Integer } count && count.Value<long>() >= 1))
        {
            check.AddError("'schema.limit' is not a whole number from 1");
        }

        if (schema["icon"] is { } icon && icon.Type != JTokenType.String)
        {
            check.AddError("'schema.icon' is not a text");
        }
    }

    protected virtual Dictionary<string, CustomSectionSetting> ReadSettings(CustomSectionCheck check, JArray fields, string parent)
    {
        var result = new Dictionary<string, CustomSectionSetting>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Count; i++)
        {
            if (ReadSetting(check, fields[i], parent, i, result) is { } setting)
            {
                result[setting.Id] = setting;
            }
        }

        return result;
    }

    /// <returns><c>null</c> for a setting that is refused or holds no value.</returns>
    protected virtual CustomSectionSetting ReadSetting(CustomSectionCheck check, JToken token, string parent, int index,
        IDictionary<string, CustomSectionSetting> declared)
    {
        if (token is not JObject field)
        {
            check.AddError($"{DescribeSetting(parent, id: null, index)} is not an object");
            return null;
        }

        var id = GetString(field, "id");
        var type = GetString(field, "type");
        var setting = DescribeSetting(parent, id, index);
        foreach (var key in field.Properties().Select(x => x.Name).Where(x => !FieldKeys.Contains(x)))
        {
            check.AddError($"{setting} has a key a custom section's setting does not take, '{key}'");
        }

        var isGroup = type is ListFieldType or ObjectFieldType;
        if (type == null || !(LabelFieldTypes.Contains(type) || ValueFieldTypes.Contains(type) || isGroup))
        {
            check.AddError($"{setting} has a type a custom section does not take, '{type}'");
            return null;
        }

        if (isGroup && parent != null)
        {
            check.AddError($"{setting} is a {type} inside '{parent}', one level deeper than a custom section's settings go");
            return null;
        }

        if (LabelFieldTypes.Contains(type))
        {
            return null;
        }

        if (id == null || !SettingIdPattern.IsMatch(id))
        {
            check.AddError($"{setting} has no id of letters, digits and '_' that starts with a letter");
            return null;
        }

        if (declared.ContainsKey(id))
        {
            check.AddError($"{setting} has the id of another setting");
            return null;
        }

        ValidateSettingKeys(check, field, type, setting);

        var element = isGroup ? ReadElement(check, field, id, type, setting) : null;
        var isLink = type == StringFieldType && GetString(field, FormatKey) == UrlFormat;

        var options = type == SelectFieldType
            ? (field["options"] as JArray)?.OfType<JObject>().Select(x => x["value"]).OfType<JValue>()
                .Select(x => Convert.ToString(x.Value, CultureInfo.InvariantCulture)).ToList()
            : null;

        return new CustomSectionSetting(id, type, isLink, element, options);
    }

    protected virtual void ValidateSettingKeys(CustomSectionCheck check, JObject field, string type, string setting)
    {
        if (IsSet(field, "translatable") && type is not (StringFieldType or TextFieldType))
        {
            check.AddError($"{setting} is translatable, which only a string or a text is");
        }

        if (field[FormatKey] is { } format && !(type == StringFieldType && format is JValue { Type: JTokenType.String } url && (string)url == UrlFormat))
        {
            check.AddError($"{setting} has a format other than the \"url\" of a string that holds a link");
        }

        if (IsSet(field, "multiple"))
        {
            check.AddError($"{setting} is multiple, and a custom section's setting holds one value");
        }

        if (type == SelectFieldType)
        {
            if (field["options"] is not JArray { Count: > 0 } options || !options.All(IsOption))
            {
                check.AddError($"{setting} is a select without a list of options, each with a value");
            }
        }
        else if (field["options"] != null)
        {
            check.AddError($"{setting} has options, which only a select has");
        }

        if (type is not (ListFieldType or ObjectFieldType) && field["element"] != null)
        {
            check.AddError($"{setting} has an element, which only a list or an object has");
        }
    }

    protected virtual Dictionary<string, CustomSectionSetting> ReadElement(CustomSectionCheck check, JObject field, string id, string type, string setting)
    {
        if (field["element"] is JArray { Count: > 0 } fields)
        {
            return ReadSettings(check, fields, id);
        }

        check.AddError($"{setting} is {(type == ListFieldType ? "a list without the fields of its rows" : "an object without its fields")} under 'element'");

        return new Dictionary<string, CustomSectionSetting>(StringComparer.Ordinal);
    }

    protected virtual void ValidateTemplate(CustomSectionCheck check, string template)
    {
        var depthError = string.Create(CultureInfo.InvariantCulture, $"the template nests elements deeper than {MaxTemplateDepth}");
        if (GetScannedDepth(template) > MaxScannedTemplateDepth)
        {
            check.AddError(depthError);
            return;
        }

        var parser = new HtmlParser(new HtmlParserOptions { IsKeepingSourceReferences = true });
        var parseErrors = new List<HtmlErrorEvent>();
        parser.Error += (_, error) => parseErrors.Add((HtmlErrorEvent)error);

        using var document = parser.ParseDocument(DocumentStart + template);
        check.Template = template;
        // the missing doctype is the document's, not the template's
        var templateErrors = parseErrors.Where(x => x.Position.Position > DocumentStart.Length).ToList();
        if (templateErrors.Count > 0)
        {
            // the parser has mended what it reports, so the elements are not what was written
            foreach (var error in templateErrors.Take(MaxParseErrors))
            {
                var (line, column) = GetLocation(check, GetIndex(check, error.Position.Position));
                check.AddError(string.Create(CultureInfo.InvariantCulture, $"the template does not read as HTML at line {line}, column {column}: {error.Message}"));
            }

            return;
        }

        var body = document.Body;
        if (body.ChildNodes.OfType<IText>().Any(x => !string.IsNullOrWhiteSpace(x.Data)))
        {
            check.AddError("the template has text outside its root element");
        }

        var roots = body.Children.ToList();
        if (roots.Count != 1)
        {
            check.AddError(string.Create(CultureInfo.InvariantCulture,
                $"the template has {roots.Count} root elements, and has one: the section's root"));
            return;
        }

        if (roots[0].LocalName.StartsWith(CustomSectionKit.TagPrefix, StringComparison.Ordinal))
        {
            check.AddError($"the template's root is <{roots[0].LocalName}>, a kit tag, which renders a component and not the section: the root is an element of HTML");
            return;
        }

        // before the elements are walked, each in the call for its parent
        if (GetDepth(roots[0]) > MaxTemplateDepth)
        {
            check.AddError(depthError);
            return;
        }

        ValidateElement(check, roots[0], new TemplateScope(Each: null, IsRoot: true));
        ValidateHeadings(check, roots[0]);
    }

    protected virtual void ValidateElement(CustomSectionCheck check, IElement element, TemplateScope scope)
    {
        var tag = element.LocalName;
        var where = string.Create(CultureInfo.InvariantCulture, $"<{tag}> at line {GetLine(check, element)}");
        IReadOnlyDictionary<string, KitField> kitAttributes = null;
        if (tag.StartsWith(CustomSectionKit.TagPrefix, StringComparison.Ordinal))
        {
            if (GetKitTagError(check, tag) is { } error)
            {
                check.AddError($"{where} {error}");
                return;
            }

            kitAttributes = check.Kit.Tags[tag];
        }
        else if (!AllowedElements.Contains(tag))
        {
            check.AddError($"{where} is not an element a custom section can hold");
            return;
        }

        ValidateAccessibility(check, element, kitAttributes, where);

        var each = element.GetAttribute(EachAttribute) is { } eachPath ? ValidateEach(check, eachPath, where, scope) : scope.Each;
        var elementScope = scope with { Each = each };
        foreach (var attribute in element.Attributes)
        {
            ValidateAttribute(check, element, attribute.Name, attribute.Value, kitAttributes, where, elementScope);
        }

        var childScope = new TemplateScope(each, IsRoot: false);
        var holdsNothing = kitAttributes != null || element.HasAttribute(HtmlAttribute);
        var hasContent = false;
        foreach (var node in element.ChildNodes)
        {
            if (node is IElement child)
            {
                hasContent = true;
                if (!holdsNothing)
                {
                    ValidateElement(check, child, childScope);
                }
            }
            else if (node is IText text && !string.IsNullOrWhiteSpace(text.Data))
            {
                hasContent = true;
                if (!holdsNothing)
                {
                    AddText(check, ValidateBindings(check, text.Data, where, childScope));
                }
            }
        }

        if (holdsNothing && hasContent)
        {
            check.AddError(kitAttributes != null ? $"{where} has content, which a kit tag does not have" : $"{where} has data-html and content of its own");
        }
    }

    /// <summary>A screen reader lists the headings by level, so a level skipped on the way down reads as a part missing.</summary>
    protected virtual void ValidateHeadings(CustomSectionCheck check, IElement root)
    {
        var levels = root.QuerySelectorAll("*").Prepend(root)
            .Where(x => x.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            .Select(x => x.LocalName[1] - '0')
            .ToList();
        var skip = levels.Zip(levels.Skip(1)).FirstOrDefault(x => x.Second > x.First + 1);
        if (skip != default)
        {
            check.AddWarning(string.Create(CultureInfo.InvariantCulture,
                $"the template's headings go from h{skip.First} to h{skip.Second}, skipping a level, which a screen reader lists as a part missing"));
        }
    }

    /// <summary>What keeps an element from being read without the screen: the attributes are checked one by one with the others.</summary>
    protected virtual void ValidateAccessibility(CustomSectionCheck check, IElement element, IReadOnlyDictionary<string, KitField> kitAttributes, string where)
    {
        switch (element.LocalName)
        {
            case "h1":
                check.AddError($"{where} is an h1, which the page holds: a section's headings start at h2");
                break;
            case "img" when !element.HasAttribute(AltAttribute):
                check.AddError($"{where} has no alt: bind it to a string setting, or write alt=\"\" for an image that only decorates");
                break;
            case "a" when !HasName(element):
                check.AddError($"{where} is a link with nothing to read: give it text or a binding, an aria-label, or an image with an alt");
                break;
            case "vc-button" when kitAttributes?.ContainsKey(LabelAttribute) == true && string.IsNullOrWhiteSpace(element.GetAttribute(LabelAttribute)):
                check.AddWarning($"{where} has no label, so a screen reader names the button by its icon or not at all");
                break;
        }
    }

    /// <summary>A kit tag is one the storefront names in its catalog, as a module of the storefront may add its own.</summary>
    protected virtual string GetKitTagError(CustomSectionCheck check, string tag)
    {
        if (!check.Kit.HasCatalog)
        {
            return "is a kit tag, and no schema catalog names the storefront's kit tags: save it with the catalog of the storefront's preview";
        }

        if (check.Kit.Tags == null)
        {
            return "is a kit tag, and the storefront offers none: its catalog has no kit";
        }

        if (!check.Kit.Tags.TryGetValue(tag, out var attributes))
        {
            return "is not a kit tag the storefront offers";
        }

        // an older catalog may hold a kit its upload would refuse now
        return GetUnknownField(attributes) is { } unknown
            ? $"is a kit tag the catalog declares with the attribute '{unknown.Key}' of a type the server does not know, '{unknown.Value.Type}'"
            : null;
    }

    /// <param name="kitAttributes">The attributes of the kit tag the element is; <c>null</c> for an element of HTML.</param>
    protected virtual void ValidateAttribute(CustomSectionCheck check, IElement element, string name, string value,
        IReadOnlyDictionary<string, KitField> kitAttributes, string where, TemplateScope scope)
    {
        switch (name)
        {
            case EachAttribute:
                return;
            case IfAttribute:
                ValidateIf(check, value, where, scope);
                return;
            case HtmlAttribute:
                ValidateHtml(check, value, kitAttributes != null, where, scope);
                return;
        }

        if (kitAttributes != null)
        {
            if (CustomSectionKit.IsDeniedAttribute(name))
            {
                check.AddError($"{where} has '{name}', which no kit tag takes, whatever the catalog declares");
            }
            else if (kitAttributes.TryGetValue(name, out var field))
            {
                ValidateKitValue(check, element.LocalName, field, name, value, where, scope);
            }
            else
            {
                check.AddError($"{where} has an attribute {element.LocalName} does not take, '{name}'");
            }

            return;
        }

        if (FindBehavior(check, name) is { } behavior)
        {
            ValidateBehavior(check, element, behavior.Name, behavior.Option, name, value, where, scope);
        }
        else if (name is "href")
        {
            ValidateLink(check, name, value, where, scope);
        }
        else if (ElementAttributes.TryGetValue(element.LocalName, out var own) && own.Contains(name))
        {
            switch (name)
            {
                case "cite":
                    ValidateLink(check, name, value, where, scope);
                    break;
                case "poster":
                    ValidateImage(check, name, value, where, scope);
                    break;
                default:
                    ValidateBindings(check, value, where, scope);
                    break;
            }
        }
        else if (name is "class")
        {
            ValidateClass(check, value, where, scope);
        }
        else if (name is "target" or "rel")
        {
            ValidateLinkTarget(check, name, value, where);
        }
        else if (name is "src" or "srcset")
        {
            ValidateImage(check, name, value, where, scope);
        }
        else if (name == AltAttribute)
        {
            ValidateAlt(check, value, where, scope);
        }
        else if (AllowedAttributes.Contains(name) || name.StartsWith("aria-", StringComparison.Ordinal))
        {
            var text = ValidateBindings(check, value, where, scope);
            if (TextAttributes.Contains(name))
            {
                AddText(check, text);
            }
        }
        else
        {
            check.AddError($"{where} has an attribute a custom section cannot use, '{name}'{GetAttributeReason(check, name)}");
        }
    }

    protected virtual string GetAttributeReason(CustomSectionCheck check, string name) => name switch
    {
        "id" => ": two sections on a page would share it",
        "style" => ": the section's CSS styles it",
        _ when name.StartsWith("on", StringComparison.Ordinal) => ": it runs script",
        _ when !name.StartsWith(DataPrefix, StringComparison.Ordinal) => string.Empty,
        _ when CustomSectionKit.IsReservedBehavior(name[DataPrefix.Length..]) => ": the storefront writes data-sb- and data-pb- attributes itself",
        _ when !check.Kit.HasCatalog => ": it is no binding, and no schema catalog names the storefront's behaviors",
        _ when check.Kit.Behaviors == null => ": it is no binding, and the storefront offers no behaviors, as its catalog has no kit",
        _ => ": it is no binding and no behavior the storefront offers",
    };

    /// <returns>The list the element repeats for, as far as it is known.</returns>
    protected virtual CustomSectionSetting ValidateEach(CustomSectionCheck check, string value, string where, TemplateScope scope)
    {
        if (scope.IsRoot)
        {
            check.AddError($"{where} is the root and has data-each, which repeats an element inside the root");
        }
        else if (scope.Each != null)
        {
            check.AddError($"{where} has data-each inside an element that has one");
        }

        var match = PathPattern.Match(value.Trim());
        if (!match.Success || match.Groups[1].Value != SettingsKey || match.Groups[3].Success)
        {
            check.AddError($"{where} has data-each=\"{Quote(value)}\", which is not settings.<id> of a list setting");
            return CustomSectionSetting.UnknownList;
        }

        var id = match.Groups[2].Value;
        if (!check.Settings.TryGetValue(id, out var setting))
        {
            check.AddError($"{where} repeats for '{id}', a setting the schema does not declare");
            return CustomSectionSetting.UnknownList;
        }

        if (setting.Type != ListFieldType)
        {
            check.AddError($"{where} repeats for '{id}', a {setting.Type} and not a list");
            return CustomSectionSetting.UnknownList;
        }

        return setting;
    }

    protected virtual void ValidateIf(CustomSectionCheck check, string value, string where, TemplateScope scope)
    {
        var path = value.Trim();
        ResolvePath(check, path.StartsWith('!') ? path[1..].TrimStart() : path, where, scope);
    }

    protected virtual void ValidateHtml(CustomSectionCheck check, string value, bool isKitTag, string where, TemplateScope scope)
    {
        if (isKitTag)
        {
            check.AddError($"{where} has data-html, and a kit tag holds nothing");
            return;
        }

        var path = value.Trim();
        if (ResolvePath(check, path, where, scope) is { } setting && setting.Type != TextFieldType)
        {
            check.AddError($"{where} has data-html=\"{Quote(path)}\", a {setting.Type}; it names a text setting, whose value is HTML");
        }
    }

    /// <summary>A value of a kit tag's attribute or of a behavior's option, checked by the type the storefront declares for it.</summary>
    /// <param name="owner">The kit tag or the behavior attribute, which names what takes the value.</param>
    protected virtual void ValidateKitValue(CustomSectionCheck check, string owner, KitField field, string name, string value, string where,
        TemplateScope scope)
    {
        // a component reads to, href and src as a link whatever type the catalog gives them
        if (field.Type == CustomSectionKit.LinkType || CustomSectionKit.IsLinkAttribute(name))
        {
            ValidateLink(check, name, value, where, scope);
            return;
        }

        if (field.Type is not (CustomSectionKit.NumberType or CustomSectionKit.BooleanType or CustomSectionKit.SelectType or CustomSectionKit.DateType))
        {
            var text = ValidateBindings(check, value, where, scope);
            if (TextAttributes.Contains(name))
            {
                AddText(check, text);
            }

            return;
        }

        if (GetSingleBinding(value) is { } path)
        {
            ResolveValue(check, path, where, scope);
        }
        else if (HasBinding(value))
        {
            check.AddError($"{where} has {name}=\"{Quote(value)}\", which is a value or one binding");
        }
        else if (GetKitValueRule(field, value) is { } rule)
        {
            check.AddError($"{where} has {name}=\"{Quote(value)}\", which {owner} does not take: it takes {rule}");
        }
    }

    /// <returns>What the field takes, when the value is not one of it; <c>null</c> for a value it takes.</returns>
    protected virtual string GetKitValueRule(KitField field, string value)
    {
        switch (field.Type)
        {
            case CustomSectionKit.BooleanType:
                return value is "" or "true" or "false" ? null : "true, false or no value";
            case CustomSectionKit.SelectType:
                return field.Options == null || field.Options.Contains(value, StringComparer.Ordinal) ? null : string.Join(", ", field.Options);
            case CustomSectionKit.DateType:
                return IsIsoDate(value) ? null : "an ISO 8601 date, or a date and a time, such as 2026-12-31 or 2026-12-31T23:59:59Z";
            default:
                var isNumber = decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number);
                return isNumber && !(number < field.Min) && !(number > field.Max) ? null : DescribeNumber(field);
        }
    }

    /// <summary>
    /// A behavior's option is an attribute of its own, data-{behavior}-{option}, on the element with the behavior or inside it: the countdown's
    /// parts are elements inside the countdown. The behavior's own value is checked by the type it declares, and is the storefront's to read
    /// when it declares none.
    /// </summary>
    protected virtual void ValidateBehavior(CustomSectionCheck check, IElement element, string behavior, string option, string name, string value,
        string where, TemplateScope scope)
    {
        var attribute = DataPrefix + behavior;
        var kitBehavior = check.Kit.Behaviors[behavior];
        var fields = kitBehavior.Value == null ? kitBehavior.Options : kitBehavior.Options.Append(KeyValuePair.Create("value", kitBehavior.Value));
        if (GetUnknownField(fields) is { } unknown)
        {
            check.AddError($"{where} has {name}, and the catalog declares {attribute} with '{unknown.Key}' of a type the server does not know, '{unknown.Value.Type}'");
            return;
        }

        if (option == null)
        {
            if (kitBehavior.Value is { } valueField)
            {
                ValidateKitValue(check, attribute, valueField, name, value, where, scope);
            }
            else
            {
                ValidateBindings(check, value, where, scope);
            }

            return;
        }

        if (!kitBehavior.Options.TryGetValue(option, out var field))
        {
            check.AddError($"{where} has {name}, which is not an option {attribute} takes");
            return;
        }

        if (!HasBehavior(element, attribute))
        {
            check.AddError($"{where} has {name}, an option of {attribute}, outside an element with {attribute}");
        }

        ValidateKitValue(check, attribute, field, name, value, where, scope);
    }

    /// <summary>The behavior a data- attribute switches on, with the option it sets; the longest name wins, so count-up-duration is an option of count-up.</summary>
    protected virtual (string Name, string Option)? FindBehavior(CustomSectionCheck check, string attribute)
    {
        if (check.Kit.Behaviors == null || !attribute.StartsWith(DataPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var name = attribute[DataPrefix.Length..];
        if (check.Kit.Behaviors.ContainsKey(name))
        {
            return (name, null);
        }

        var behavior = check.Kit.Behaviors.Keys.Where(x => name.StartsWith(x + "-", StringComparison.Ordinal)).MaxBy(x => x.Length);

        return behavior == null ? null : (behavior, name[(behavior.Length + 1)..]);
    }

    /// <summary>The kit of the storefront's catalog: its kit tags, which start with vc-, and its behaviors, each with the fields it takes.</summary>
    protected virtual StorefrontKit ReadKit(JObject catalog)
    {
        if (catalog?[KitKey] is not JObject kit)
        {
            return new StorefrontKit(catalog != null, Tags: null, Behaviors: null);
        }

        var tags = ReadKitEntries(kit["tags"], CustomSectionKit.TagPrefix, x => ReadKitFields(x["attributes"]));
        // a stored catalog may name a behavior its upload would refuse now
        var behaviors = ReadKitEntries(kit["behaviors"], string.Empty,
                x => new KitBehavior(x["value"] is JObject value ? ReadKitField(value) : null, ReadKitFields(x["options"])))
            .Where(x => !CustomSectionKit.IsReservedBehavior(x.Key))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

        return new StorefrontKit(true, tags, behaviors);
    }

    /// <summary>
    /// A class is the type's own: the storefront's utility classes would style the section from outside its CSS, such as fixed over the
    /// page. A binding names a select whose options are the type's own classes.
    /// </summary>
    protected virtual void ValidateClass(CustomSectionCheck check, string value, string where, TemplateScope scope)
    {
        var literal = BindingPattern.Replace(value, match =>
        {
            var path = match.Groups[1].Value.Trim();
            if (ResolveValue(check, path, where, scope) is { } setting &&
                (setting.Type != SelectFieldType || setting.Options == null || !setting.Options.All(x => IsOwnClasses(check.Type, x))))
            {
                check.AddError($"{where} binds class to '{path}', which is not a select whose options are the type's own classes");
            }

            return " ";
        });

        if (HasBinding(literal) || literal.Contains("}}", StringComparison.Ordinal))
        {
            check.AddError($"{where} has a '{{{{' or a '}}}}' that is no binding, such as {{{{ settings.title }}}}");
        }

        foreach (var name in literal.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries).Where(x => !IsOwnClasses(check.Type, x)).Distinct(StringComparer.Ordinal))
        {
            check.AddError($"{where} has the class '{Quote(name)}', which is not the type's own: '{check.Type}', or a name starting with '{check.Type}-' or '{check.Type}__'");
        }
    }

    /// <summary>An image's alt is read in the page's language, so it binds a string setting; a literal alt is saved with the text warning.</summary>
    protected virtual void ValidateAlt(CustomSectionCheck check, string value, string where, TemplateScope scope)
    {
        var literal = BindingPattern.Replace(value, match =>
        {
            var path = match.Groups[1].Value.Trim();
            if (ResolveValue(check, path, where, scope) is { } setting && setting.Type != StringFieldType)
            {
                check.AddError($"{where} has alt bound to '{path}', a {setting.Type} and not a string setting");
            }

            return " ";
        });

        if (HasBinding(literal) || literal.Contains("}}", StringComparison.Ordinal))
        {
            check.AddError($"{where} has a '{{{{' or a '}}}}' that is no binding, such as {{{{ settings.title }}}}");
        }

        AddText(check, literal);
    }

    /// <summary>A link opens a page in this window or a new one, and gives it no hold on the storefront's window; the value is written as it is.</summary>
    protected virtual void ValidateLinkTarget(CustomSectionCheck check, string name, string value, string where)
    {
        if (HasBinding(value))
        {
            check.AddError($"{where} has {name}=\"{Quote(value)}\", which is written as it is, without a binding");
        }
        else if (name == "target" && !LinkTargets.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            check.AddError($"{where} has target=\"{Quote(value)}\", which is not _self or _blank");
        }
        else if (name == "rel" && value.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries).Contains(OpenerRel, StringComparer.OrdinalIgnoreCase))
        {
            check.AddError($"{where} has rel=\"{Quote(value)}\" with opener, which gives the page it opens a hold on the storefront's window");
        }
    }

    protected virtual void ValidateLink(CustomSectionCheck check, string name, string value, string where, TemplateScope scope)
    {
        if (GetSingleBinding(value) is { } path)
        {
            if (ResolveValue(check, path, where, scope) is { } setting && !setting.IsLink)
            {
                check.AddError($"{where} has {name} bound to '{path}', which is not a link setting: a string with \"format\": \"url\"");
            }
        }
        else if (HasBinding(value))
        {
            check.AddError($"{where} has {name}=\"{Quote(value)}\", which is a link or one binding of a link setting");
        }
        else if (!IsLink(value))
        {
            check.AddError($"{where} has {name}=\"{Quote(value)}\", which is not a path starting with '/', or an https:, mailto: or tel: URL");
        }
    }

    protected virtual void ValidateImage(CustomSectionCheck check, string name, string value, string where, TemplateScope scope)
    {
        if (GetSingleBinding(value) is { } path)
        {
            if (ResolveValue(check, path, where, scope) is { } setting && setting.Type != ImagesFieldType)
            {
                check.AddError($"{where} has {name} bound to '{path}', a {setting.Type} and not an images setting");
            }

            return;
        }

        if (HasBinding(value))
        {
            check.AddError($"{where} has {name}=\"{Quote(value)}\", which is a URL or one binding of an images setting");
            return;
        }

        // a srcset is a list of URLs, each with its width or density after it
        var urls = name == "srcset"
            ? value.Split(',').Select(x => x.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty)
            : [value];
        foreach (var url in urls.Where(x => !IsAssetUrl(x)))
        {
            check.AddError($"{where} has {name} with '{Quote(url)}', which is not a URL of the store's asset library");
        }
    }

    /// <summary>Checks the bindings in a text or an attribute value; returns the text without them.</summary>
    protected virtual string ValidateBindings(CustomSectionCheck check, string text, string where, TemplateScope scope)
    {
        var literal = BindingPattern.Replace(text, match =>
        {
            ResolveValue(check, match.Groups[1].Value.Trim(), where, scope);
            return " ";
        });

        if (HasBinding(literal) || literal.Contains("}}", StringComparison.Ordinal))
        {
            check.AddError($"{where} has a '{{{{' or a '}}}}' that is no binding, such as {{{{ settings.title }}}}");
        }

        return literal;
    }

    /// <summary>A path whose value is inserted: a list is repeated with data-each, and an object is bound by its fields.</summary>
    protected virtual CustomSectionSetting ResolveValue(CustomSectionCheck check, string path, string where, TemplateScope scope)
    {
        var setting = ResolvePath(check, path, where, scope);
        if (setting?.Element != null)
        {
            check.AddError(setting.Type == ListFieldType
                ? $"{where} binds '{path}', a list, which data-each repeats an element for"
                : $"{where} binds '{path}', an object, whose fields are bound one by one");
        }

        return setting;
    }

    /// <returns>The setting the path names; <c>null</c> when it names none, or one of a list that is not known.</returns>
    protected virtual CustomSectionSetting ResolvePath(CustomSectionCheck check, string path, string where, TemplateScope scope)
    {
        var match = PathPattern.Match(path);
        if (!match.Success)
        {
            check.AddError($"{where} binds '{Quote(path)}', which is not settings.<id>, settings.<id>.<field> or item.<field>");
            return null;
        }

        var id = match.Groups[2].Value;
        var field = match.Groups[3];
        if (match.Groups[1].Value != SettingsKey)
        {
            if (scope.Each == null)
            {
                check.AddError($"{where} binds '{path}' outside an element with data-each, where there is no item");
                return null;
            }

            if (field.Success)
            {
                check.AddError($"{where} binds '{path}', deeper than the fields of a row");
                return null;
            }

            if (scope.Each.Element == null)
            {
                return null;
            }

            if (!scope.Each.Element.TryGetValue(id, out var rowField))
            {
                check.AddError($"{where} binds '{path}', a field the rows of '{scope.Each.Id}' do not have");
            }

            return rowField;
        }

        if (!check.Settings.TryGetValue(id, out var setting))
        {
            check.AddError($"{where} binds '{path}', a setting the schema does not declare");
            return null;
        }

        if (!field.Success)
        {
            return setting;
        }

        if (setting.Type != ObjectFieldType)
        {
            check.AddError($"{where} binds '{path}', but '{id}' is a {setting.Type} and not an object");
            return null;
        }

        if (!setting.Element.TryGetValue(field.Value, out var objectField))
        {
            check.AddError($"{where} binds '{path}', a field '{id}' does not have");
        }

        return objectField;
    }

    /// <summary>
    /// The CSS is checked as a browser reads it, from its tokens: a comment in a string, an escape or a quote in a URL reads as the browser
    /// reads it. The storefront wraps the CSS in blocks of its own, so a block left open or closed twice is refused before anything else.
    /// </summary>
    protected virtual void ValidateCss(CustomSectionCheck check, string css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return;
        }

        var tokens = CssTokenizer.Tokenize(css);
        if (GetCssStructureError(tokens) is { } error)
        {
            check.AddError(error);
            return;
        }

        ValidateCssTokens(check, tokens);
    }

    /// <returns>The error of a token no browser reads as written, of a block without its pair or nested too deep; <c>null</c> for none.</returns>
    protected virtual string GetCssStructureError(IList<CssTokenizer.Token> tokens)
    {
        // each open block with the token that closes it, and whether an at-rule opened it
        var blocks = new Stack<(CssTokenizer.TokenKind Closer, bool AtRule)>();
        var atRules = 0;
        var inAtRulePrelude = false;
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case CssTokenizer.TokenKind.BadString:
                    return "the CSS has a string that a line break cuts off";
                case CssTokenizer.TokenKind.BadUrl:
                    return "the CSS has a url( whose URL a quote, a parenthesis or a space breaks: quote the URL";
                case CssTokenizer.TokenKind.AtKeyword:
                    inAtRulePrelude = true;
                    break;
                case CssTokenizer.TokenKind.Semicolon:
                    inAtRulePrelude = false;
                    break;
                case CssTokenizer.TokenKind.OpenCurly:
                    blocks.Push((CssTokenizer.TokenKind.CloseCurly, inAtRulePrelude));
                    atRules += inAtRulePrelude ? 1 : 0;
                    inAtRulePrelude = false;
                    break;
                case CssTokenizer.TokenKind.OpenParen or CssTokenizer.TokenKind.Function:
                    blocks.Push((CssTokenizer.TokenKind.CloseParen, false));
                    break;
                case CssTokenizer.TokenKind.OpenSquare:
                    blocks.Push((CssTokenizer.TokenKind.CloseSquare, false));
                    break;
                case CssTokenizer.TokenKind.CloseCurly or CssTokenizer.TokenKind.CloseParen or CssTokenizer.TokenKind.CloseSquare:
                    if (blocks.Count == 0 || blocks.Peek().Closer != token.Kind)
                    {
                        return $"the CSS has a '{token.Value}' without its pair";
                    }

                    atRules -= blocks.Pop().AtRule ? 1 : 0;
                    break;
            }

            if (blocks.Count > MaxCssDepth)
            {
                return string.Create(CultureInfo.InvariantCulture, $"the CSS nests blocks deeper than {MaxCssDepth}");
            }

            if (atRules > MaxAtRuleDepth)
            {
                return string.Create(CultureInfo.InvariantCulture, $"the CSS nests at-rules deeper than {MaxAtRuleDepth}");
            }
        }

        return blocks.Count == 0 ? null : $"the CSS has a '{GetOpener(blocks.Peek().Closer)}' without its pair";
    }

    /// <summary>The at-rules, the URLs, and the declarations that run script or take the section out of the page, custom properties included.</summary>
    protected virtual void ValidateCssTokens(CustomSectionCheck check, IList<CssTokenizer.Token> tokens)
    {
        var errors = new List<string>();
        var directions = new List<string>();
        // the function or the block each token is in, the functions that load an image it is in, and whether each rule's block is for one
        // direction, as :dir(rtl) { } is
        var blocks = new Stack<CssTokenizer.Token>();
        var imageFunctions = new Stack<string>();
        var directionRules = new Stack<bool>();
        var preludeNamesDirection = false;
        // a declaration starts a block or follows a ';' or a nested rule; an ident anywhere else is part of a value or a selector
        var atDeclarationStart = false;
        // the tokens since the last block or ';': a rule's selector, or an at-rule's prelude, once a '{' follows
        var prelude = new List<CssTokenizer.Token>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var block = blocks.Count > 0 ? blocks.Peek() : default;
            switch (token.Kind)
            {
                case CssTokenizer.TokenKind.Function when string.Equals(token.Value, "dir", StringComparison.OrdinalIgnoreCase):
                    preludeNamesDirection = true;
                    break;
                case CssTokenizer.TokenKind.Semicolon or CssTokenizer.TokenKind.CloseCurly:
                    preludeNamesDirection = false;
                    break;
                case CssTokenizer.TokenKind.AtKeyword:
                    ValidateCssAtRule(check, tokens, i, errors);
                    break;
                case CssTokenizer.TokenKind.Url:
                    AddCssUrlError(check, token.Value, errors);
                    break;
                case CssTokenizer.TokenKind.String when imageFunctions.Count > 0:
                    AddCssUrlError(check, token.Value, errors);
                    break;
                case CssTokenizer.TokenKind.Function when string.Equals(token.Value, "expression", StringComparison.OrdinalIgnoreCase):
                    errors.Add("the CSS has expression(), which runs script");
                    break;
                case CssTokenizer.TokenKind.Function when IsLateValueFunction(token.Value) && imageFunctions.Count > 0:
                    errors.Add($"the CSS has {token.Value.ToLowerInvariant()}() inside {imageFunctions.Peek()}(), whose URL is then not known on save");
                    break;
                case CssTokenizer.TokenKind.Ident when block.Kind == CssTokenizer.TokenKind.OpenCurly && atDeclarationStart:
                    ValidateCssDeclaration(check, tokens, i, errors, directionRules.Peek() ? null : directions);
                    break;
            }

            if (token.Kind != CssTokenizer.TokenKind.Whitespace)
            {
                atDeclarationStart = token.Kind is CssTokenizer.TokenKind.OpenCurly or CssTokenizer.TokenKind.Semicolon or CssTokenizer.TokenKind.CloseCurly;
            }

            if (token.Kind == CssTokenizer.TokenKind.OpenCurly)
            {
                directionRules.Push((directionRules.Count > 0 && directionRules.Peek()) || preludeNamesDirection);
                preludeNamesDirection = false;
                ValidateCssSelector(prelude, errors);
            }

            if (token.Kind is CssTokenizer.TokenKind.OpenCurly or CssTokenizer.TokenKind.CloseCurly or CssTokenizer.TokenKind.Semicolon)
            {
                prelude.Clear();
            }
            else
            {
                prelude.Add(token);
            }

            if (token.Kind is CssTokenizer.TokenKind.OpenCurly or CssTokenizer.TokenKind.OpenParen or CssTokenizer.TokenKind.OpenSquare or
                CssTokenizer.TokenKind.Function)
            {
                blocks.Push(token);
                if (token.Kind == CssTokenizer.TokenKind.Function && ImageFunctions.Contains(token.Value))
                {
                    imageFunctions.Push(token.Value.ToLowerInvariant());
                }
            }
            else if (token.Kind is CssTokenizer.TokenKind.CloseCurly or CssTokenizer.TokenKind.CloseParen or CssTokenizer.TokenKind.CloseSquare)
            {
                var closed = blocks.Pop();
                if (closed.Kind == CssTokenizer.TokenKind.Function && ImageFunctions.Contains(closed.Value))
                {
                    imageFunctions.Pop();
                }

                if (token.Kind == CssTokenizer.TokenKind.CloseCurly)
                {
                    directionRules.Pop();
                }
            }
        }

        foreach (var error in errors.Distinct(StringComparer.Ordinal))
        {
            check.AddError(error);
        }

        foreach (var direction in directions.Distinct(StringComparer.Ordinal))
        {
            check.AddWarning(direction);
        }

        if (check.Animates && !HasReducedMotionQuery(tokens))
        {
            check.AddWarning("the CSS animates with no @media (prefers-reduced-motion: reduce) block, so it moves for people who ask for less motion");
        }
    }

    /// <summary>
    /// A rule's selector may not name :root, html or body. The storefront scopes the CSS to the section, where they match nothing, but a
    /// browser without @scope would apply them to the page.
    /// </summary>
    protected virtual void ValidateCssSelector(IList<CssTokenizer.Token> prelude, IList<string> errors)
    {
        var first = prelude.FirstOrDefault(x => x.Kind != CssTokenizer.TokenKind.Whitespace);
        if (first.Kind is CssTokenizer.TokenKind.None or CssTokenizer.TokenKind.AtKeyword)
        {
            return;
        }

        var attributes = 0;
        var previous = default(CssTokenizer.Token);
        foreach (var token in prelude.Where(x => x.Kind != CssTokenizer.TokenKind.Whitespace))
        {
            attributes += token.Kind switch { CssTokenizer.TokenKind.OpenSquare => 1, CssTokenizer.TokenKind.CloseSquare => -1, _ => 0 };
            var isDocument = attributes == 0 && token.Kind == CssTokenizer.TokenKind.Ident && (previous.Kind == CssTokenizer.TokenKind.Colon
                ? string.Equals(token.Value, "root", StringComparison.OrdinalIgnoreCase)
                : !(previous.Kind == CssTokenizer.TokenKind.Delim && previous.Value is "." or "|") && token.Value.ToLowerInvariant() is "html" or "body");
            if (isDocument)
            {
                errors.Add($"the CSS selector '{Quote(WriteCss(prelude))}' reaches :root, html or body, outside the section");
                return;
            }

            previous = token;
        }
    }

    protected virtual void ValidateCssAtRule(CustomSectionCheck check, IList<CssTokenizer.Token> tokens, int index, IList<string> errors)
    {
        var name = tokens[index].Value;
        if (!AllowedAtRules.Contains(name))
        {
            errors.Add($"the CSS has @{name}, and a custom section's CSS has only {AllowedAtRulesText}");
            return;
        }

        if (!string.Equals(name, KeyframesAtRule, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // keyframes names are not scoped, and a type's name may start another's, so the type's own start with the type and '__'
        var prefix = check.Type + KeyframesSeparator;
        var keyframes = tokens.Skip(index + 1).FirstOrDefault(x => x.Kind != CssTokenizer.TokenKind.Whitespace);
        if (keyframes.Kind is not (CssTokenizer.TokenKind.Ident or CssTokenizer.TokenKind.String))
        {
            errors.Add("the CSS has @keyframes without a name");
        }
        else if (keyframes.Value.Length <= prefix.Length || !keyframes.Value.StartsWith(prefix, StringComparison.Ordinal))
        {
            errors.Add($"the CSS names @keyframes '{Quote(keyframes.Value)}', which does not start with '{prefix}': keyframes names are not scoped");
        }
    }

    /// <summary>A declaration is a name and a colon in a block; a name, a colon and a block of its own is a nested rule, such as a:hover { }.</summary>
    /// <param name="directions">Takes a warning for a side of the page that stays when the page reads right to left; <c>null</c> in a rule for one direction.</param>
    protected virtual void ValidateCssDeclaration(CustomSectionCheck check, IList<CssTokenizer.Token> tokens, int index, IList<string> errors,
        IList<string> directions)
    {
        var colon = index + 1;
        while (colon < tokens.Count && tokens[colon].Kind == CssTokenizer.TokenKind.Whitespace)
        {
            colon++;
        }

        if (colon >= tokens.Count || tokens[colon].Kind != CssTokenizer.TokenKind.Colon)
        {
            return;
        }

        var value = new List<CssTokenizer.Token>();
        var depth = 0;
        for (var i = colon + 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (depth == 0 && token.Kind is CssTokenizer.TokenKind.Semicolon or CssTokenizer.TokenKind.CloseCurly)
            {
                break;
            }

            if (depth == 0 && token.Kind == CssTokenizer.TokenKind.OpenCurly)
            {
                return;
            }

            depth += token.Kind switch
            {
                CssTokenizer.TokenKind.OpenCurly or CssTokenizer.TokenKind.OpenParen or CssTokenizer.TokenKind.OpenSquare or CssTokenizer.TokenKind.Function => 1,
                CssTokenizer.TokenKind.CloseCurly or CssTokenizer.TokenKind.CloseParen or CssTokenizer.TokenKind.CloseSquare => -1,
                _ => 0,
            };
            value.Add(token);
        }

        var name = tokens[index].Value;
        check.Animates |= MotionProperties.Contains(name);
        if (HasLiteralColor(name, value))
        {
            check.ColorProperties.Add(name.ToLowerInvariant());
        }

        if (directions != null && GetLogicalDirection(name, value) is { } logical)
        {
            directions.Add($"the CSS sets {logical.Physical}, which stays on that side when the page reads right to left: use {logical.Logical}");
        }

        if (ScriptProperties.Contains(name))
        {
            errors.Add($"the CSS sets {name.ToLowerInvariant()}, which runs script");
        }
        else if (RemovesFocusRing(name, value))
        {
            errors.Add($"the CSS sets {name.ToLowerInvariant()} to none or 0, which hides where the keyboard is on the page");
        }
        else if (string.Equals(name, "position", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Exists(x => x.Kind == CssTokenizer.TokenKind.Ident && string.Equals(x.Value, "fixed", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("the CSS has position: fixed, which puts the section over the page");
            }
            else if (value.Find(x => x.Kind == CssTokenizer.TokenKind.Function && IsLateValueFunction(x.Value)) is { Value: { } function })
            {
                errors.Add($"the CSS sets position with {function.ToLowerInvariant()}(), which could make it fixed");
            }
        }
    }

    /// <summary>An image of the CSS may also be written into it as a data: URL, as an image loads no script and nothing else.</summary>
    protected virtual void AddCssUrlError(CustomSectionCheck check, string url, IList<string> errors)
    {
        if (!IsAssetUrl(url) && !ImageDataUrlPattern.IsMatch(url))
        {
            errors.Add($"the CSS loads '{Quote(url)}', which is not a URL of the store's asset library or a data:image/ URL");
        }
    }

    protected virtual void AddWarnings(CustomSectionCheck check)
    {
        if (check.Texts.Count > 0)
        {
            var texts = string.Join(", ", check.Texts.Distinct(StringComparer.Ordinal).Take(MaxSamples).Select(x => $"'{Quote(x)}'"));
            check.AddWarning($"the template holds text of its own ({texts}), which is the same in every language: bind it to a translatable setting");
        }

        if (check.ColorProperties.Count > 0)
        {
            var properties = string.Join(", ", check.ColorProperties.Distinct(StringComparer.Ordinal).Take(MaxSamples));
            check.AddWarning($"the CSS sets literal colors ({properties}), which the palette does not change: use var(--color-…) or a color-token setting's variable");
        }
    }

    /// <summary>
    /// A URL of the asset library starts with its public address, as the link of each of its files does, once its dot segments are resolved
    /// as a browser resolves them: '/../' or an escaped dot could otherwise climb out of the library on the same host.
    /// </summary>
    protected virtual bool IsAssetUrl(string url)
    {
        if (GetAssetsBaseUrl() is not { } baseUrl || url.Any(char.IsControl) || url.Contains('\\'))
        {
            return false;
        }

        var unescaped = EscapedPathPattern.Replace(url, x => x.Value.ToLowerInvariant() switch { "%2e" => ".", "%2f" => "/", _ => "\\" });
        if (unescaped.Contains('\\') || EscapedPathPattern.IsMatch(unescaped))
        {
            return false;
        }

        return Uri.TryCreate(unescaped, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo) &&
            Uri.TryCreate(baseUrl, UriKind.Absolute, out var library) && uri.AbsoluteUri.StartsWith(library.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The public link of a file at the library's root without the file; <c>null</c> when the library gives none.</summary>
    protected virtual string GetAssetsBaseUrl()
    {
        string url;
        try
        {
            url = blobUrlResolver?.GetAbsoluteUrl(AssetProbe);
        }
        catch (UriFormatException)
        {
            return null;
        }

        return url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && url.EndsWith("/" + AssetProbe, StringComparison.Ordinal)
            ? url[..^AssetProbe.Length]
            : null;
    }

    /// <summary>A URL parser drops tabs and line breaks, and reads '\' as '/', so either could make a path start with '//', another site.</summary>
    protected virtual bool IsLink(string url)
    {
        if (url.Length == 0 || url.Any(char.IsControl))
        {
            return false;
        }

        if (url.StartsWith('/'))
        {
            return url.Length == 1 || url[1] is not ('/' or '\\');
        }

        if (url.StartsWith('#'))
        {
            return AnchorPattern.IsMatch(url);
        }

        return LinkSchemes.Any(x => url.StartsWith(x, StringComparison.OrdinalIgnoreCase)) && Uri.TryCreate(url, UriKind.Absolute, out _);
    }

    private static string GetPart(CustomSectionCheck check, JObject file, string key)
    {
        if (file[key] is not JValue { Type: JTokenType.String } value)
        {
            check.AddError($"'{key}' is not a text");
            return null;
        }

        var text = (string)value;
        var size = Encoding.UTF8.GetByteCount(text);
        if (size > MaxPartSize)
        {
            check.AddError(string.Create(CultureInfo.InvariantCulture, $"'{key}' is {FormatSize(size)}, more than {FormatSize(MaxPartSize)}"));
            return null;
        }

        return text;
    }

    private static void AddText(CustomSectionCheck check, string text)
    {
        // text without a letter, such as '+' after a number, reads the same in every language
        if (text.Any(char.IsLetter))
        {
            check.Texts.Add(string.Join(' ', text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries)));
        }
    }

    private static int GetDepth(IElement root)
    {
        var deepest = 0;
        var elements = new Stack<(IElement Element, int Depth)>([(root, 1)]);
        while (elements.TryPop(out var current))
        {
            deepest = Math.Max(deepest, current.Depth);
            foreach (var child in current.Element.Children)
            {
                elements.Push((child, current.Depth + 1));
            }
        }

        return deepest;
    }

    // the depth of the tags as written, without the parser: an element whose end tag is left out counts as open
    private static int GetScannedDepth(string template)
    {
        var open = new Stack<string>();
        var deepest = 0;
        for (var start = template.IndexOf('<'); start >= 0 && start < template.Length; start = template.IndexOf('<', start))
        {
            if (template.AsSpan(start).StartsWith("<!--"))
            {
                var commentEnd = template.IndexOf("-->", start + 4, StringComparison.Ordinal);
                start = commentEnd < 0 ? template.Length : commentEnd + 3;
                continue;
            }

            var isEndTag = start + 1 < template.Length && template[start + 1] == '/';
            var nameStart = start + (isEndTag ? 2 : 1);
            var nameEnd = nameStart;
            while (nameEnd < template.Length && (char.IsAsciiLetterOrDigit(template[nameEnd]) || template[nameEnd] == '-'))
            {
                nameEnd++;
            }

            if (nameEnd == nameStart || !char.IsAsciiLetter(template[nameStart]))
            {
                start++;
                continue;
            }

            var tagEnd = FindTagEnd(template, nameEnd);
            var isSelfClosing = tagEnd >= 2 && template[tagEnd - 1] == '>' && template[tagEnd - 2] == '/';
            var name = template[nameStart..nameEnd].ToLowerInvariant();
            // an end tag closes the element it names and those left open inside it; one that names no open element closes nothing
            if (isEndTag && open.Contains(name))
            {
                while (open.Pop() != name)
                {
                }
            }
            else if (!isEndTag && !isSelfClosing && !VoidElements.Contains(name))
            {
                open.Push(name);
                deepest = Math.Max(deepest, open.Count);
            }

            start = tagEnd;
        }

        return deepest;
    }

    // past the '>' that ends a tag, outside its quoted attribute values
    private static int FindTagEnd(string template, int start)
    {
        var quote = '\0';
        for (var i = start; i < template.Length; i++)
        {
            if (quote != '\0')
            {
                quote = template[i] == quote ? '\0' : quote;
            }
            else if (template[i] is '"' or '\'')
            {
                quote = template[i];
            }
            else if (template[i] == '>')
            {
                return i + 1;
            }
        }

        return template.Length;
    }

    private static bool IsOwnClasses(string type, string classes) =>
        classes.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries)
            .All(x => x == type || ClassSeparators.Any(separator => x.StartsWith(type + separator, StringComparison.Ordinal)));

    private static (string Physical, string Logical)? GetLogicalDirection(string name, List<CssTokenizer.Token> value)
    {
        var property = name.ToLowerInvariant();
        if (LogicalProperties.TryGetValue(property, out var logicalProperty))
        {
            return (property, logicalProperty);
        }

        if (!LogicalValues.TryGetValue(property, out var logicalValues))
        {
            return null;
        }

        var side = value.Find(x => x.Kind == CssTokenizer.TokenKind.Ident && x.Value.ToLowerInvariant() is "left" or "right").Value?.ToLowerInvariant();

        return side == null ? null : ($"{property}: {side}", $"{property}: {(side == "left" ? logicalValues.Left : logicalValues.Right)}");
    }

    // a mask takes only the alpha of its colors, so black and transparent there are no colors of the design
    private static bool HasLiteralColor(string name, List<CssTokenizer.Token> value)
    {
        var property = name.ToLowerInvariant();
        if ((property.StartsWith("-webkit-", StringComparison.Ordinal) ? property[8..] : property).StartsWith("mask", StringComparison.Ordinal))
        {
            return false;
        }

        var takesNamedColors = !property.StartsWith("--", StringComparison.Ordinal) && ColorPropertyPattern.IsMatch(property);
        for (var i = 0; i < value.Count; i++)
        {
            var token = value[i];
            var isLiteral = token.Kind switch
            {
                CssTokenizer.TokenKind.Hash => true,
                CssTokenizer.TokenKind.Function => ColorFunctions.Contains(token.Value) && !IsPaletteRelativeColor(value, i),
                CssTokenizer.TokenKind.Ident => takesNamedColors && NamedColors.Contains(token.Value),
                _ => false,
            };
            if (isLiteral)
            {
                return true;
            }
        }

        return false;
    }

    // a relative color from the palette, rgb(from var(--color-primary-500) r g b / 0.25), changes with it
    private static bool IsPaletteRelativeColor(List<CssTokenizer.Token> value, int function)
    {
        var arguments = value.Skip(function + 1).Where(x => x.Kind != CssTokenizer.TokenKind.Whitespace).Take(3).ToList();
        if (arguments.Count < 2 || arguments[0].Kind != CssTokenizer.TokenKind.Ident || !string.Equals(arguments[0].Value, "from", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return arguments[1] switch
        {
            { Kind: CssTokenizer.TokenKind.Ident } origin => origin.Value.ToLowerInvariant() is "currentcolor" or "transparent",
            { Kind: CssTokenizer.TokenKind.Function } origin when string.Equals(origin.Value, "var", StringComparison.OrdinalIgnoreCase) =>
                arguments.Count > 2 && arguments[2].Kind == CssTokenizer.TokenKind.Ident &&
                (arguments[2].Value.StartsWith("--color-", StringComparison.Ordinal) || arguments[2].Value.StartsWith("--setting-", StringComparison.Ordinal)),
            _ => false,
        };
    }

    // the tokens as CSS, for an error to quote
    private static string WriteCss(IEnumerable<CssTokenizer.Token> tokens) =>
        string.Concat(tokens.Select(x => x.Kind switch
        {
            CssTokenizer.TokenKind.Function => x.Value + "(",
            CssTokenizer.TokenKind.AtKeyword => "@" + x.Value,
            CssTokenizer.TokenKind.Hash => "#" + x.Value,
            CssTokenizer.TokenKind.String => $"\"{x.Value}\"",
            CssTokenizer.TokenKind.Url => $"url({x.Value})",
            _ => x.Value,
        })).Trim();

    private static bool IsLateValueFunction(string name) => LateValueFunctions.Contains(name) || name.StartsWith("--", StringComparison.Ordinal);

    private static bool RemovesFocusRing(string name, List<CssTokenizer.Token> value) => name.ToLowerInvariant() switch
    {
        "outline" => value.Exists(x => IsNone(x) || IsZero(x)),
        "outline-style" => value.Exists(IsNone),
        "outline-width" => value.Exists(IsZero),
        _ => false,
    };

    private static bool IsNone(CssTokenizer.Token token) =>
        token.Kind == CssTokenizer.TokenKind.Ident && string.Equals(token.Value, "none", StringComparison.OrdinalIgnoreCase);

    // 0, or a length of 0 such as 0px
    private static bool IsZero(CssTokenizer.Token token)
    {
        if (token.Kind is not (CssTokenizer.TokenKind.Number or CssTokenizer.TokenKind.Dimension))
        {
            return false;
        }

        var number = new string(token.Value.TakeWhile(x => char.IsAsciiDigit(x) || x is '.' or '+' or '-').ToArray());

        return decimal.TryParse(number, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var zero) && zero == 0;
    }

    // a media query on prefers-reduced-motion, reduce or no-preference, in any block of the CSS
    private static bool HasReducedMotionQuery(IList<CssTokenizer.Token> tokens)
    {
        var meaningful = tokens.Where(x => x.Kind != CssTokenizer.TokenKind.Whitespace).ToList();

        return meaningful.Zip(meaningful.Skip(1), meaningful.Skip(2)).Any(x =>
            x.First.Kind == CssTokenizer.TokenKind.Ident && string.Equals(x.First.Value, "prefers-reduced-motion", StringComparison.OrdinalIgnoreCase) &&
            x.Second.Kind == CssTokenizer.TokenKind.Colon &&
            x.Third.Kind == CssTokenizer.TokenKind.Ident && ReducedMotionValues.Contains(x.Third.Value));
    }

    private static char GetOpener(CssTokenizer.TokenKind closer) => closer switch
    {
        CssTokenizer.TokenKind.CloseCurly => '{',
        CssTokenizer.TokenKind.CloseParen => '(',
        _ => '[',
    };

    private static string GetSingleBinding(string value)
    {
        var match = SingleBindingPattern.Match(value);

        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static bool HasBinding(string value) => value.Contains("{{", StringComparison.Ordinal);

    // an element of SVG keeps no place of its own, so it is looked for after its parent's
    private static int GetLine(CustomSectionCheck check, IElement element)
    {
        var placed = element;
        while (placed.SourceReference == null && placed.ParentElement != null)
        {
            placed = placed.ParentElement;
        }

        var index = GetIndex(check, placed.SourceReference?.Position.Position ?? 0);
        if (placed != element && check.Template.IndexOf("<" + element.LocalName, index, StringComparison.OrdinalIgnoreCase) is >= 0 and var found)
        {
            index = found;
        }

        return GetLocation(check, index).Line;
    }

    // the parser counts lines without the line breaks of indentation, while its place in the text is right
    private static int GetIndex(CustomSectionCheck check, int position) => Math.Clamp(position - 1 - DocumentStart.Length, 0, check.Template.Length);

    private static (int Line, int Column) GetLocation(CustomSectionCheck check, int index)
    {
        var lineStarts = check.LineStarts;
        var found = lineStarts.BinarySearch(index);
        var line = found >= 0 ? found : ~found - 1;

        return (line + 1, index - lineStarts[line] + 1);
    }

    private static bool IsName(JToken name) => name switch
    {
        JValue { Type: JTokenType.String } text => !string.IsNullOrWhiteSpace((string)text),
        JObject translations => translations.Count > 0 && translations.Properties().All(x => x.Value is JValue { Type: JTokenType.String } text && !string.IsNullOrWhiteSpace((string)text)),
        _ => false,
    };

    private static bool IsOption(JToken option) =>
        option is JObject { } item && item["value"] is JValue { Type: JTokenType.String or JTokenType.Integer or JTokenType.Float };

    private static bool IsSet(JObject field, string name) => field[name] is JValue { Type: JTokenType.Boolean } flag && (bool)flag;

    private static string GetString(JObject item, string name) => item[name] is JValue { Type: JTokenType.String } value ? (string)value : null;

    private static string DescribeSetting(string parent, string id, int index) => (parent, id) switch
    {
        (null, null) => string.Create(CultureInfo.InvariantCulture, $"setting {index + 1}"),
        (null, _) => $"setting '{id}'",
        (_, null) => string.Create(CultureInfo.InvariantCulture, $"setting {index + 1} of '{parent}'"),
        _ => $"setting '{parent}.{id}'",
    };

    private static KeyValuePair<string, KitField>? GetUnknownField(IEnumerable<KeyValuePair<string, KitField>> fields) =>
        fields.Where(x => !CustomSectionKit.FieldTypes.Contains(x.Value.Type ?? string.Empty)).Cast<KeyValuePair<string, KitField>?>().FirstOrDefault();

    // text or a binding, which the storefront fills with text, an aria-label, rich text, or an image whose alt names it
    private static bool HasName(IElement link) =>
        !string.IsNullOrWhiteSpace(link.TextContent) ||
        !string.IsNullOrWhiteSpace(link.GetAttribute("aria-label")) ||
        link.HasAttribute(HtmlAttribute) ||
        link.QuerySelectorAll("[data-html]").Length > 0 ||
        link.QuerySelectorAll("img").Any(x => !string.IsNullOrWhiteSpace(x.GetAttribute(AltAttribute)));

    private static bool HasBehavior(IElement element, string attribute)
    {
        for (var current = element; current != null; current = current.ParentElement)
        {
            if (current.HasAttribute(attribute))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, T> ReadKitEntries<T>(JToken entries, string prefix, Func<JObject, T> read) =>
        (entries as JObject)?.Properties()
            .Where(x => x.Name.StartsWith(prefix, StringComparison.Ordinal) && x.Name.Length > prefix.Length && x.Value is JObject)
            .ToDictionary(x => x.Name, x => read((JObject)x.Value), StringComparer.Ordinal)
        ?? new Dictionary<string, T>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, KitField> ReadKitFields(JToken fields) =>
        (fields as JObject)?.Properties()
            .Where(x => x.Value is JObject)
            .ToDictionary(x => x.Name, x => ReadKitField((JObject)x.Value), StringComparer.Ordinal)
        ?? new Dictionary<string, KitField>(StringComparer.Ordinal);

    // an option is a value, or an object with its value and its label
    private static KitField ReadKitField(JObject field) => new(
        GetString(field, "type"),
        (field["options"] as JArray)?
            .Select(x => x is JObject option ? option["value"] : x)
            .OfType<JValue>()
            .Select(x => Convert.ToString(x.Value, CultureInfo.InvariantCulture))
            .ToList(),
        GetNumber(field, "min"),
        GetNumber(field, "max"));

    private static bool IsIsoDate(string value) =>
        IsoDatePattern.IsMatch(value) && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    private static decimal? GetNumber(JObject item, string name) =>
        item[name] is JValue { Type: JTokenType.Integer or JTokenType.Float } value ? value.Value<decimal>() : null;

    private static string DescribeNumber(KitField field) => (field.Min, field.Max) switch
    {
        ({ } min, { } max) => string.Create(CultureInfo.InvariantCulture, $"a number from {FormatNumber(min)} to {FormatNumber(max)}"),
        ({ } min, null) => string.Create(CultureInfo.InvariantCulture, $"a number from {FormatNumber(min)}"),
        (null, { } max) => string.Create(CultureInfo.InvariantCulture, $"a number up to {FormatNumber(max)}"),
        _ => "a number",
    };

    private static string FormatNumber(decimal number) => number.ToString("0.##########", CultureInfo.InvariantCulture);

    private static string FormatSize(long size) => string.Create(CultureInfo.InvariantCulture, $"{size / 1024.0:0.#} KB");

    // a value goes into an error shortened and on one line
    private static string Quote(string value)
    {
        var line = string.Join(' ', value.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries));

        return line.Length > MaxQuoteLength ? line[..MaxQuoteLength] + "…" : line;
    }

    /// <summary>A setting a binding can name; <see cref="Element"/> holds the fields of a list's rows or of an object.</summary>
    /// <param name="Options">The values of a select's options.</param>
    protected sealed record CustomSectionSetting(string Id, string Type, bool IsLink, IReadOnlyDictionary<string, CustomSectionSetting> Element,
        IReadOnlyList<string> Options = null)
    {
        /// <summary>The list of a data-each that names none: the items of its rows are not checked.</summary>
        public static CustomSectionSetting UnknownList { get; } = new(string.Empty, ListFieldType, IsLink: false, Element: null);
    }

    /// <param name="Each">The list the element repeats for, or one of its ancestors does.</param>
    protected sealed record TemplateScope(CustomSectionSetting Each, bool IsRoot);

    /// <param name="Type">string, number, boolean, select, date or link; any other refuses the kit tag or the behavior that declares it.</param>
    /// <param name="Options">The values a select takes.</param>
    protected sealed record KitField(string Type, IReadOnlyList<string> Options, decimal? Min, decimal? Max);

    /// <param name="HasCatalog">A catalog was given: without one, nothing is known of the kit.</param>
    /// <param name="Tags">The attributes of each kit tag, by its name; <c>null</c> for a catalog without a kit.</param>
    /// <param name="Behaviors">Each behavior by its name; <c>null</c> for a catalog without a kit.</param>
    protected sealed record StorefrontKit(bool HasCatalog, IReadOnlyDictionary<string, IReadOnlyDictionary<string, KitField>> Tags,
        IReadOnlyDictionary<string, KitBehavior> Behaviors);

    /// <param name="Value">The type of the behavior attribute's own value; <c>null</c> when it declares none.</param>
    protected sealed record KitBehavior(KitField Value, IReadOnlyDictionary<string, KitField> Options);

    protected sealed class CustomSectionCheck(string type, CustomSectionValidationResult result)
    {
        private string _template = string.Empty;

        public string Type { get; } = type;

        public string File { get; } = ThemeStorage.GetCustomSectionFile(type);

        public StorefrontKit Kit { get; init; }

        /// <summary>The settings a binding can name, by id; <c>null</c> when the schema cannot be read.</summary>
        public IDictionary<string, CustomSectionSetting> Settings { get; set; }

        /// <summary>The template as written, which names the line of an element.</summary>
        public string Template
        {
            get => _template;
            set
            {
                _template = value;
                LineStarts = [0, .. value.Select((x, i) => (x, i)).Where(x => x.x == '\n').Select(x => x.i + 1)];
            }
        }

        /// <summary>Where each line of <see cref="Template"/> starts.</summary>
        public List<int> LineStarts { get; private set; } = [0];

        public IList<string> Texts { get; } = [];

        public IList<string> ColorProperties { get; } = [];

        /// <summary>The CSS has an animation or a transition.</summary>
        public bool Animates { get; set; }

        public int ErrorCount => result.Errors.Count;

        public void AddError(string error) => result.Errors.Add($"{File}: {error}");

        public void AddWarning(string warning) => result.Warnings.Add($"{File}: {warning}");
    }
}
