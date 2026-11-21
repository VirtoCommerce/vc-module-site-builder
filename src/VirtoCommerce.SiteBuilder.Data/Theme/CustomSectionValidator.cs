using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using static VirtoCommerce.SiteBuilder.Data.Theme.CustomSectionText;
using static VirtoCommerce.SiteBuilder.Data.Theme.ThemeContentReader;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>Refuses what the storefront would leave out of a type, naming the rule (docs/contracts/custom-sections.md).</summary>
public class CustomSectionValidator(
    CustomSectionTemplateValidator templateValidator,
    CustomSectionCssValidator cssValidator,
    ILogger<CustomSectionValidator> logger) : ICustomSectionValidator
{
    private const int MaxPartSize = 64 * 1024;
    private const int MaxTotalSize = 256 * 1024;
    private const int MaxCssDepth = 32;
    private const int MaxTemplateDepth = 32;
    private const int MaxAtRuleDepth = 4;
    private const string KeyframesSeparator = "__";

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
    private const string ListFieldType = CustomSectionSetting.ListType;
    private const string ObjectFieldType = "object";
    private const string UrlFormat = "url";

    private static readonly HashSet<string> TypeKeys = new(StringComparer.Ordinal) { FormatKey, NameKey, SchemaKey, TemplateKey, CssKey };

    // no 'visibility' and no 'request': the designer runs the one and sends the other
    private static readonly string[] FieldKeys =
    [
        "id", "type", "label", "default", "translatable", "tab", "info", "placeholder", "required", "multiline", "multiple", "format", "options",
        "element", "min", "max", "step",
    ];

    // the fields the designer shows as text to read, which hold no value
    private static readonly HashSet<string> LabelFieldTypes = new(StringComparer.Ordinal) { "header", "paragraph" };

    private static readonly string[] ValueFieldTypes =
    [
        StringFieldType, TextFieldType, "number", "slider", "checkbox", SelectFieldType, "color-token", ImagesFieldType, "calendar",
    ];

    private static readonly HashSet<string> FormControls = new(StringComparer.Ordinal)
    {
        "form", "input", "select", "textarea", "button", "option", "optgroup", "datalist", "fieldset", "legend", "label", "output",
    };

    private static readonly string[] AllowedAttributes =
    [
        "class", "role", "alt", "title", "lang", "dir", "hidden", "href", "target", "rel", "src", "srcset", "sizes", "loading", "decoding",
        "width", "height", "datetime", "colspan", "rowspan", "scope",
    ];

    private static readonly Dictionary<string, string[]> ElementAttributes = new(StringComparer.Ordinal)
    {
        ["video"] = ["autoplay", "muted", "loop", "playsinline", "controls", "preload", "poster"],
        ["source"] = ["type", "media"],
        ["details"] = ["open"],
        ["blockquote"] = ["cite"],
        ["q"] = ["cite"],
    };

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

    private static readonly Dictionary<string, (string Left, string Right)> LogicalValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text-align"] = ("start", "end"),
        ["float"] = ("inline-start", "inline-end"),
        ["clear"] = ("inline-start", "inline-end"),
    };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex SettingIdPattern = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.None, RegexTimeout);

    private CustomSectionRules _rules;
    private Regex _typePattern;

    protected CustomSectionRules Rules => _rules ??= CreateRules();

    private Regex TypePattern => _typePattern ??= new Regex(Rules.TypePattern, RegexOptions.None, RegexTimeout);

    public virtual CustomSectionValidationResult Validate(string type, JToken content, JObject catalog)
    {
        var result = AbstractTypeFactory<CustomSectionValidationResult>.TryCreateInstance();
        var check = new CustomSectionCheck(type, Rules, CustomSectionKit.Read(catalog), result);
        if (type.Length > Rules.MaxTypeLength || !TypePattern.IsMatch(type))
        {
            check.AddError($"'{type}' is not a custom section type, which {ThemeCustomSections.DescribeType(Rules)}");
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

        return result;
    }

    public virtual string GetSizeError(IDictionary<string, JToken> types)
    {
        var size = types.Values.Where(x => x != null).Sum(x => (long)Encoding.UTF8.GetByteCount(x.ToString(Formatting.None)));
        if (size <= Rules.MaxTotalSize)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{ThemeStorage.CustomSectionsFolder}: the custom section types are {FormatSize(size)} of JSON together, more than {FormatSize(Rules.MaxTotalSize)}: the snapshot carries every type to every page");
    }

    public virtual CustomSectionRules GetRules() => Rules;

    protected virtual CustomSectionRules CreateRules()
    {
        var result = AbstractTypeFactory<CustomSectionRules>.TryCreateInstance();
        result.TypePattern = ThemeStorage.CustomSectionTypePattern;
        result.MaxTypeLength = ThemeStorage.CustomSectionTypeMaxLength;
        result.Elements = SiteBuilderHtmlSanitizer.AllowedTags.Where(x => !FormControls.Contains(x)).Order(StringComparer.Ordinal).ToList();
        result.Attributes = AllowedAttributes.Order(StringComparer.Ordinal).ToList();
        result.BindingAttributes = ["data-each", "data-if", "data-html"];
        result.TextAttributes = ["alt", "aria-label", "label", "title"];
        result.LinkSchemes = ["https:", "mailto:", "tel:"];
        result.LinkTargets = ["_self", "_blank"];
        result.RefusedRels = ["opener"];
        result.ClassSeparators = ["-", "__"];
        result.SchemaKeys = [SettingsKey, "targetTemplates", "limit", "icon"];
        result.FieldTypes = [.. LabelFieldTypes, .. ValueFieldTypes, ListFieldType, ObjectFieldType];
        result.FieldKeys = [.. FieldKeys];
        result.AtRules = ["media", "supports", "container", "starting-style", "keyframes"];
        result.KeyframesSeparator = KeyframesSeparator;
        result.ElementAttributes = ElementAttributes.ToDictionary(x => x.Key, x => (IList<string>)[.. x.Value], StringComparer.Ordinal);
        result.ReservedDataPrefixes = [.. CustomSectionKit.ReservedBehaviorPrefixes.Select(x => "data-" + x)];
        result.ScriptProperties = ["-moz-binding", "behavior"];
        result.ImageFunctions = ["url", "src", "image", "image-set", "-webkit-image-set", "cross-fade", "-webkit-cross-fade"];
        result.LateValueFunctions = ["var", "attr", "env", "if"];
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
        foreach (var key in schema.Properties().Select(x => x.Name).Where(x => !check.Rules.SchemaKeys.Contains(x)))
        {
            check.AddError($"'schema.{key}' is not a key of a custom section's schema, which has {JoinAnd(check.Rules.SchemaKeys)}");
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
        foreach (var key in field.Properties().Select(x => x.Name).Where(x => !check.Rules.FieldKeys.Contains(x)))
        {
            check.AddError($"{setting} has a key a custom section's setting does not take, '{key}'");
        }

        var isGroup = type is ListFieldType or ObjectFieldType;
        if (type == null || !check.Rules.FieldTypes.Contains(type))
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
            if (field["options"] is not JArray { Count: > 0 } options || !options.All(IsSettingOption))
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

    protected virtual void ValidateTemplate(CustomSectionCheck check, string template) => templateValidator.Validate(check, template);

    protected virtual void ValidateCss(CustomSectionCheck check, string css) => cssValidator.Validate(check, css);

    private static string GetPart(CustomSectionCheck check, JObject file, string key)
    {
        if (file[key] is not JValue { Type: JTokenType.String } value)
        {
            check.AddError($"'{key}' is not a text");
            return null;
        }

        var text = (string)value;
        var size = Encoding.UTF8.GetByteCount(text);
        if (size > check.Rules.MaxPartSize)
        {
            check.AddError(string.Create(CultureInfo.InvariantCulture, $"'{key}' is {FormatSize(size)}, more than {FormatSize(check.Rules.MaxPartSize)}"));
            return null;
        }

        return text;
    }

    private static bool IsName(JToken name) => name switch
    {
        JValue { Type: JTokenType.String } text => !string.IsNullOrWhiteSpace((string)text),
        JObject translations => translations.Count > 0 && translations.Properties().All(x => x.Value is JValue { Type: JTokenType.String } text && !string.IsNullOrWhiteSpace((string)text)),
        _ => false,
    };

    private static bool IsSettingOption(JToken option) =>
        option is JObject { } item && item["value"] is JValue { Type: JTokenType.String or JTokenType.Integer or JTokenType.Float };

    private static bool IsSet(JObject field, string name) => field[name] is JValue { Type: JTokenType.Boolean } flag && (bool)flag;

    private static string DescribeSetting(string parent, string id, int index) => (parent, id) switch
    {
        (null, null) => string.Create(CultureInfo.InvariantCulture, $"setting {index + 1}"),
        (null, _) => $"setting '{id}'",
        (_, null) => string.Create(CultureInfo.InvariantCulture, $"setting {index + 1} of '{parent}'"),
        _ => $"setting '{parent}.{id}'",
    };

    private static string FormatSize(long size) => string.Create(CultureInfo.InvariantCulture, $"{size / 1024.0:0.#} KB");
}
