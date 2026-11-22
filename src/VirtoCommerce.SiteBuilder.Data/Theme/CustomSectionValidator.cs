using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Services;
using static VirtoCommerce.SiteBuilder.Data.Theme.CustomSectionFields;
using static VirtoCommerce.SiteBuilder.Data.Theme.CustomSectionText;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>Refuses what the storefront would leave out of a type, naming the rule (docs/contracts/custom-sections.md).</summary>
public class CustomSectionValidator(
    CustomSectionTemplateValidator templateValidator,
    CustomSectionCssValidator cssValidator,
    CustomSectionUrlPolicy urlPolicy,
    IOptions<ThemeSnapshotOptions> options,
    ILogger<CustomSectionValidator> logger) : ICustomSectionValidator
{
    /// <summary>UTF-8 bytes, for the template and the CSS each.</summary>
    public const int MaxPartSize = 64 * 1024;

    private const string NameKey = "name";
    private const string SchemaKey = "schema";
    private const string TemplateKey = "template";
    private const string CssKey = "css";
    private const string HeaderType = "header";
    private const string ParagraphType = "paragraph";

    public static IReadOnlyList<string> SchemaKeys { get; } = [SettingsKey, "targetTemplates", "limit", "icon"];

    public static IReadOnlyList<string> FieldKeys { get; } =
    [
        "id", "type", "label", "default", "translatable", "tab", "info", "placeholder", "required", "multiline", "multiple", FormatKey, "options",
        "element", "min", "max", "step",
    ];

    public static IReadOnlyList<string> FieldTypes { get; } =
    [
        HeaderType, ParagraphType, StringType, TextType, "number", "slider", "checkbox", SelectType, "color-token", ImagesType, "calendar", ListType,
        ObjectType,
    ];

    private static readonly string[] TextKeys = ["label", "info", "placeholder"];

    private static readonly HashSet<string> TypeKeys = new(StringComparer.Ordinal) { FormatKey, NameKey, SchemaKey, TemplateKey, CssKey };

    // the fields the designer shows as text to read, which hold no value
    private static readonly HashSet<string> LabelFieldTypes = new(StringComparer.Ordinal) { HeaderType, ParagraphType };

    private static readonly Regex SettingIdPattern = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.None, TimeSpan.FromSeconds(1));

    public virtual CustomSectionValidationResult Validate(string type, JToken content, JObject catalog) =>
        ValidateType(type, content, new Lazy<StorefrontKit>(() => CustomSectionKit.Read(catalog)));

    public virtual IList<CustomSectionValidationResult> ValidateTypes(IDictionary<string, JToken> types, ICollection<string> checkedTypes, JObject catalog)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(checkedTypes);

        var kit = new Lazy<StorefrontKit>(() => CustomSectionKit.Read(catalog));
        var results = checkedTypes.Select(x => ValidateType(x, types.TryGetValue(x, out var content) ? content : null, kit)).ToList();
        if (GetSizeError(types) is { } sizeError)
        {
            var result = AbstractTypeFactory<CustomSectionValidationResult>.TryCreateInstance();
            result.File = ThemeStorage.CustomSectionsFolder;
            result.Errors.Add(sizeError);
            results.Add(result);
        }

        return results;
    }

    public virtual CustomSectionRules GetRules()
    {
        var result = AbstractTypeFactory<CustomSectionRules>.TryCreateInstance();
        result.TypePattern = ThemeStorage.CustomSectionTypePattern;
        result.MaxTypeLength = ThemeStorage.CustomSectionTypeMaxLength;
        result.Elements = CustomSectionTemplateValidator.Elements.Order(StringComparer.Ordinal).ToList();
        result.Attributes = CustomSectionTemplateValidator.Attributes.Order(StringComparer.Ordinal).ToList();
        result.BindingAttributes = [.. CustomSectionTemplateValidator.BindingAttributes];
        result.TextAttributes = CustomSectionTemplateValidator.TextAttributes.Order(StringComparer.Ordinal).ToList();
        result.LinkSchemes = [.. CustomSectionTemplateValidator.LinkSchemes];
        result.LinkTargets = [.. CustomSectionTemplateValidator.LinkTargets];
        result.RefusedRels = [.. CustomSectionTemplateValidator.RefusedRels];
        result.ClassSeparators = [.. CustomSectionTemplateValidator.ClassSeparators];
        result.ElementAttributes = CustomSectionTemplateValidator.ElementAttributes
            .ToDictionary(x => x.Key, x => (IList<string>)[.. x.Value], StringComparer.Ordinal);
        result.ReservedDataPrefixes = [.. CustomSectionKit.ReservedBehaviorPrefixes.Select(x => "data-" + x)];
        result.SchemaKeys = [.. SchemaKeys];
        result.FieldTypes = [.. FieldTypes];
        result.FieldKeys = [.. FieldKeys];
        result.AtRules = [.. CustomSectionCssValidator.AtRules];
        result.KeyframesHolders = [.. CustomSectionCssValidator.KeyframesHolders];
        result.KeyframesSeparator = CustomSectionCssValidator.KeyframesSeparator;
        result.ScriptProperties = CustomSectionCssValidator.ScriptProperties.Order(StringComparer.Ordinal).ToList();
        result.RefusedFunctions = [.. CustomSectionCssValidator.RefusedFunctions.Keys];
        result.PositionValues = [.. CustomSectionCssValidator.PositionValues];
        result.ImageFunctions = [.. CustomSectionCssValidator.ImageFunctions];
        result.LateValueFunctions = [.. CustomSectionCssValidator.LateValueFunctions];
        result.LogicalProperties = CustomSectionCssValidator.LogicalProperties
            .Concat(CustomSectionCssValidator.LogicalValues.SelectMany(x => new[]
            {
                KeyValuePair.Create($"{x.Key}: left", $"{x.Key}: {x.Value.Left}"),
                KeyValuePair.Create($"{x.Key}: right", $"{x.Key}: {x.Value.Right}"),
            }))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        result.KitFieldTypes = [.. CustomSectionKit.FieldTypes];
        result.KitDeniedAttributes = [.. CustomSectionKit.DeniedAttributePrefixes.Select(x => x + "*"), .. CustomSectionKit.DeniedAttributes];
        result.KitLinkAttributes = [.. CustomSectionKit.LinkAttributes];
        result.MaxTemplateDepth = CustomSectionTemplateValidator.MaxDepth;
        result.MaxTemplateTags = CustomSectionTemplateValidator.MaxTags;
        result.MaxTemplateAttributes = CustomSectionTemplateValidator.MaxAttributes;
        result.KitLimits = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["tags"] = CustomSectionKit.MaxTags,
            ["behaviors"] = CustomSectionKit.MaxBehaviors,
            ["fields"] = CustomSectionKit.MaxFields,
            ["options"] = CustomSectionKit.MaxOptions,
            ["option_label_length"] = CustomSectionKit.MaxOptionLabelLength,
            ["writes"] = CustomSectionKit.MaxWrites,
            ["write_length"] = CustomSectionKit.MaxWriteLength,
            ["name_length"] = CustomSectionKit.MaxNameLength,
            ["description_length"] = CustomSectionKit.MaxDescriptionLength,
            ["design_entries"] = CustomSectionKit.MaxDesignEntries,
            ["design_text_length"] = CustomSectionKit.MaxDesignTextLength,
            ["icons"] = CustomSectionKit.MaxIcons,
            ["icon_length"] = CustomSectionKit.MaxIconLength,
            ["guide_length"] = CustomSectionKit.MaxGuideLength,
        };
        result.MaxCssDepth = CustomSectionCssValidator.MaxDepth;
        result.MaxAtRuleDepth = CustomSectionCssValidator.MaxAtRuleDepth;
        result.MaxPartSize = MaxPartSize;
        result.MaxTotalSize = options.Value.CustomSectionsMaxSize;

        return result;
    }

    /// <param name="kit">Read once for all the types a call checks, in the check of each, so that a fault refuses the types instead of the request.</param>
    protected virtual CustomSectionValidationResult ValidateType(string type, JToken content, Lazy<StorefrontKit> kit)
    {
        var result = AbstractTypeFactory<CustomSectionValidationResult>.TryCreateInstance();
        var check = new CustomSectionCheck(type, result);
        result.File = check.File;
        if (!ThemeStorage.IsCustomSectionType(type))
        {
            check.AddError($"'{type}' is not a custom section type, which {ThemeCustomSections.DescribeType()}");
            return result;
        }

        if (content is not JObject file)
        {
            check.AddError("not an object");
            return result;
        }

        try
        {
            check.Kit = kit.Value;
            ValidateShape(check, file);
            if (GetPart(check, file, TemplateKey) is { } template && check.Settings != null)
            {
                templateValidator.Validate(check, template);
            }

            if (GetPart(check, file, CssKey) is { } css)
            {
                cssValidator.Validate(check, css);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            check.AddError("the type takes too long to check: write its template and its CSS plainer");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The custom section type {Type} could not be checked.", type);
            check.AddError("the type could not be checked, so it is not saved: the platform's log has the cause");
        }

        return result;
    }

    /// <returns><c>null</c> when the types together fit what the snapshot carries to every page.</returns>
    protected virtual string GetSizeError(IDictionary<string, JToken> types)
    {
        var size = types.Values.Where(x => x != null).Sum(x => (long)Encoding.UTF8.GetByteCount(x.ToString(Formatting.None)));
        var maxSize = options.Value.CustomSectionsMaxSize;
        if (size <= maxSize)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"the custom section types are {FormatSize(size)} of JSON together, more than {FormatSize(maxSize)}: the snapshot carries every type to every page");
    }

    protected virtual void ValidateShape(CustomSectionCheck check, JObject file)
    {
        foreach (var key in file.Properties().Select(x => x.Name).Where(x => !TypeKeys.Contains(x)))
        {
            check.AddError($"'{key}' is not a key of a custom section type, which has format, name, schema, template and css");
        }

        if (file[FormatKey] is not JValue { Value: 1L })
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
            check.AddError($"'schema.{key}' is not a key of a custom section's schema, which has {JoinAnd(SchemaKeys)}");
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

        if (schema["limit"] is { } limit && limit is not JValue { Value: >= 1L })
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

        var id = ThemeJson.GetString(field, "id");
        var type = ThemeJson.GetString(field, "type");
        var setting = DescribeSetting(parent, id, index);
        foreach (var key in field.Properties().Select(x => x.Name).Where(x => !FieldKeys.Contains(x)))
        {
            check.AddError($"{setting} has a key a custom section's setting does not take, '{key}'");
        }

        // the designer shows these as they are
        foreach (var key in TextKeys.Where(x => field[x] is { Type: not (JTokenType.String or JTokenType.Null) }))
        {
            check.AddError($"{setting} has {(key == "info" ? "an" : "a")} {key} that is not a text");
        }

        var isGroup = type is ListType or ObjectType;
        if (type == null || !FieldTypes.Contains(type))
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

        // the designer writes a value under the setting's id, next to the section's own keys
        if (SiteBuilderPageItems.ItemKeys.Contains(id, StringComparer.Ordinal))
        {
            check.AddError($"{setting} has the id of a key a section holds itself: {JoinOr(SiteBuilderPageItems.ItemKeys)}");
            return null;
        }

        if (declared.ContainsKey(id))
        {
            check.AddError($"{setting} has the id of another setting");
            return null;
        }

        ValidateSettingKeys(check, field, type, setting);

        var element = isGroup ? ReadElement(check, field, id, type, setting) : null;
        var isLink = IsLink(type, field);

        var options = type == SelectType
            ? (field["options"] as JArray)?.OfType<JObject>().Select(x => x["value"]).OfType<JValue>()
                .Select(x => Convert.ToString(x.Value, CultureInfo.InvariantCulture)).ToList()
            : null;

        return new CustomSectionSetting(id, type, isLink, element, options);
    }

    protected virtual void ValidateSettingKeys(CustomSectionCheck check, JObject field, string type, string setting)
    {
        if (IsSet(field, "translatable") && type is not (StringType or TextType))
        {
            check.AddError($"{setting} is translatable, which only a string or a text is");
        }

        if (field[FormatKey] is { } format && !(type == StringType && format is JValue { Type: JTokenType.String } url && (string)url == UrlFormat))
        {
            check.AddError($"{setting} has a format other than the \"url\" of a string that holds a link");
        }

        if (IsSet(field, "multiple"))
        {
            check.AddError($"{setting} is multiple, and a custom section's setting holds one value");
        }

        if (type == SelectType)
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

        if (type is not (ListType or ObjectType) && field["element"] != null)
        {
            check.AddError($"{setting} has an element, which only a list or an object has");
        }

        if (field["default"] is { Type: not JTokenType.Null } value && GetDefaultError(type, field, value) is { } error)
        {
            check.AddError($"{setting} has a default {error}");
        }
    }

    /// <summary>The storefront leaves out a default it does not take, so a save refuses it; an empty one is none.</summary>
    /// <returns><c>null</c> for a default the setting takes.</returns>
    protected virtual string GetDefaultError(string type, JObject field, JToken value)
    {
        if (value is JValue { Type: JTokenType.String } empty && ((string)empty).Length == 0)
        {
            return null;
        }

        if (type == SelectType)
        {
            var options = (field["options"] as JArray)?.OfType<JObject>().Select(x => x["value"]).Where(x => x != null).ToList() ?? [];
            return options.Exists(x => JToken.DeepEquals(x, value)) ? null : "that is not one of its options";
        }

        // a translatable value is a text per language
        var texts = (value is JObject translations ? translations.Properties().Select(x => x.Value) : [value])
            .Select(x => x is JValue { Type: JTokenType.String } text ? (string)text : null)
            .ToList();
        if (IsLink(type, field) && texts.Exists(x => x == null || (x.Length > 0 && !urlPolicy.IsLink(x, CustomSectionTemplateValidator.LinkSchemes))))
        {
            return $"that is not a path starting with '/', or an {JoinOr(CustomSectionTemplateValidator.LinkSchemes)} URL";
        }

        return type == ImagesType && texts.Exists(x => x == null || (x.Length > 0 && !urlPolicy.IsAssetUrl(x)))
            ? "that is not a URL of the store's asset library"
            : null;
    }

    protected virtual Dictionary<string, CustomSectionSetting> ReadElement(CustomSectionCheck check, JObject field, string id, string type, string setting)
    {
        if (field["element"] is JArray { Count: > 0 } fields)
        {
            return ReadSettings(check, fields, id);
        }

        check.AddError($"{setting} is {(type == ListType ? "a list without the fields of its rows" : "an object without its fields")} under 'element'");

        return new Dictionary<string, CustomSectionSetting>(StringComparer.Ordinal);
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

    private static bool IsLink(string type, JObject field) => type == StringType && ThemeJson.GetString(field, FormatKey) == UrlFormat;

    // a text per language is the shape the snapshot translates and the designer reads; any other object would show the type's id
    private static bool IsName(JToken name) => name switch
    {
        JValue { Type: JTokenType.String } text => !string.IsNullOrWhiteSpace((string)text),
        JObject translations => ThemeTranslations.IsTranslation(translations) && translations.Properties().All(x => !string.IsNullOrWhiteSpace((string)x.Value)),
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
