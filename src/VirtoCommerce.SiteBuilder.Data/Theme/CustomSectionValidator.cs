using System.Globalization;
using System.Numerics;
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
    IOptions<ThemeSnapshotOptions> themeSnapshotOptions,
    ILogger<CustomSectionValidator> logger) : ICustomSectionValidator
{
    /// <summary>UTF-8 bytes, for the template and the CSS each.</summary>
    public const int MaxPartSize = 64 * BytesPerKilobyte;

    /// <summary>Of a theme: the snapshot carries every type to every page, and a save reads each of them.</summary>
    public const int MaxTypes = 256;

    public const int MaxNameLength = 256;

    private const int BytesPerKilobyte = 1024;

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
        HeaderType, ParagraphType, StringType, TextType, NumberType, SliderType, CheckboxType, SelectType, ColorTokenType, ImagesType, CalendarType, ListType,
        ObjectType,
    ];

    private static readonly string[] TextKeys = ["label", "info", "placeholder"];

    private static readonly HashSet<string> TypeKeys = new(StringComparer.Ordinal)
    {
        ThemeCustomSections.FormatKey, ThemeCustomSections.NameKey, ThemeCustomSections.SchemaKey, ThemeCustomSections.TemplateKey, ThemeCustomSections.CssKey,
    };

    // the fields the designer shows as text to read, which hold no value
    private static readonly HashSet<string> LabelFieldTypes = new(StringComparer.Ordinal) { HeaderType, ParagraphType };

    private static readonly Regex SettingIdPattern = new("\\A[A-Za-z][A-Za-z0-9_]*\\z", RegexOptions.None, TimeSpan.FromSeconds(1));

    public virtual CustomSectionValidationResult Validate(string type, JToken content, JObject catalog) =>
        ValidateType(type, content, new Lazy<StorefrontKit>(() => CustomSectionKit.Read(catalog)));

    public virtual IList<CustomSectionValidationResult> ValidateTypes(IDictionary<string, JToken> types, ICollection<string> checkedTypes, JObject catalog)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(checkedTypes);

        var kit = new Lazy<StorefrontKit>(() => CustomSectionKit.Read(catalog));
        var results = checkedTypes.Select(x => ValidateType(x, types.TryGetValue(x, out var content) ? content : null, kit)).ToList();
        var folderErrors = new[] { GetSizeError(types), GetCountError(types) }.Where(x => x != null).ToList();
        if (folderErrors.Count > 0)
        {
            var result = AbstractTypeFactory<CustomSectionValidationResult>.TryCreateInstance();
            result.File = ThemeStorage.CustomSectionsFolder;
            result.Errors = folderErrors;
            results.Add(result);
        }

        return results;
    }

    public virtual CustomSectionRules GetRules()
    {
        var result = AbstractTypeFactory<CustomSectionRules>.TryCreateInstance();
        result.TypePattern = ThemeStorage.CustomSectionTypePattern;
        result.ReservedSettingIds = [.. SiteBuilderPageItems.ItemKeys];
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
        result.KitImageAttributes = [.. CustomSectionKit.ImageAttributes];
        result.MaxTemplateDepth = CustomSectionTemplateValidator.MaxDepth;
        result.MaxTemplateTags = CustomSectionTemplateValidator.MaxTags;
        result.MaxTemplateAttributes = CustomSectionTemplateValidator.MaxAttributes;
        result.KitLimits = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["tags"] = CustomSectionKit.MaxTags,
            ["behaviors"] = CustomSectionKit.MaxBehaviors,
            ["fields"] = CustomSectionKit.MaxFields,
            ["options"] = CustomSectionKit.MaxOptions,
            ["option_length"] = CustomSectionKit.MaxOptionLength,
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
        result.MaxTotalSize = themeSnapshotOptions.Value.CustomSectionsMaxSize;
        result.MaxTypes = MaxTypes;

        return result;
    }

    /// <param name="kit">Read once for all the types a call checks, in the check of each, so that a fault refuses the types instead of the request.</param>
    protected virtual CustomSectionValidationResult ValidateType(string type, JToken content, Lazy<StorefrontKit> kit)
    {
        var result = AbstractTypeFactory<CustomSectionValidationResult>.TryCreateInstance();
        var check = new CustomSectionCheck(type, result);
        result.File = check.File;
        if (type == null || !ThemeStorage.IsCustomSectionType(type))
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
            if (GetPart(check, file, ThemeCustomSections.TemplateKey) is { } template && check.Settings != null)
            {
                templateValidator.Validate(check, template);
            }

            if (GetPart(check, file, ThemeCustomSections.CssKey) is { } css)
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

    public virtual long GetSize(IDictionary<string, JToken> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        return types.Values.Where(x => x != null).Sum(x => (long)Encoding.UTF8.GetByteCount(x.ToString(Formatting.None)));
    }

    /// <returns><c>null</c> when the types together fit what the snapshot carries to every page.</returns>
    protected virtual string GetSizeError(IDictionary<string, JToken> types)
    {
        var size = GetSize(types);
        var maxSize = themeSnapshotOptions.Value.CustomSectionsMaxSize;
        if (size <= maxSize)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"the custom section types are {FormatSize(size)} of JSON together, more than {FormatSize(maxSize)}: the snapshot carries every type to every page");
    }

    protected virtual string GetCountError(IDictionary<string, JToken> types) =>
        types.Count <= MaxTypes ? null : string.Create(CultureInfo.InvariantCulture,
            $"the theme holds {types.Count} custom section types, more than {MaxTypes}: the snapshot carries every type to every page");

    protected virtual void ValidateShape(CustomSectionCheck check, JObject file)
    {
        foreach (var key in file.Properties().Select(x => x.Name).Where(x => !TypeKeys.Contains(x)))
        {
            check.AddError($"'{Quote(key)}' is not a key of a custom section type, which has format, name, schema, template and css");
        }

        if (file[ThemeCustomSections.FormatKey] is not JValue { Value: (long)ThemeCustomSections.Format })
        {
            check.AddError("'format' is not 1");
        }

        if (!IsName(file[ThemeCustomSections.NameKey]))
        {
            check.AddError(string.Create(CultureInfo.InvariantCulture, $"'name' is not a text, or a text per language, of at most {MaxNameLength} characters"));
        }

        if (file[ThemeCustomSections.SchemaKey] is JObject schema)
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
            check.AddError($"'schema.{Quote(key)}' is not a key of a custom section's schema, which has {JoinAnd(SchemaKeys)}");
        }

        if (schema[SettingsKey] is JArray settings)
        {
            check.Settings = ReadSettings(check, settings, parent: null);
        }
        else
        {
            check.AddError("'schema.settings' is not a list of settings");
        }

        ValidateTargetTemplates(check, schema);

        if (schema["limit"] is { } limit && limit is not JValue { Value: >= 1L })
        {
            check.AddError("'schema.limit' is not a whole number from 1");
        }

        if (schema["icon"] is { } icon && icon.Type != JTokenType.String)
        {
            check.AddError("'schema.icon' is not a text");
        }
    }

    protected virtual void ValidateTargetTemplates(CustomSectionCheck check, JObject schema)
    {
        if (schema["targetTemplates"] is { } targets && !(targets is JArray list && list.All(x => x is JValue { Type: JTokenType.String } key && !string.IsNullOrWhiteSpace((string)key))))
        {
            check.AddError("'schema.targetTemplates' is not a list of template names");
        }
        else if (check.Kit?.TemplateKeys is { } templateKeys)
        {
            // only a warning: a catalog uploaded earlier may lack a template the storefront has now
            foreach (var key in (schema["targetTemplates"] as JArray ?? []).Select(x => (string)x).Where(x => !templateKeys.Contains(x)))
            {
                check.AddWarning($"'schema.targetTemplates' names '{Quote(key)}', which is no template key of the storefront's catalog, so no template offers " +
                    "the type: the schemas resource lists the catalog's templates");
            }
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
        ValidateFieldKeys(check, field, setting);

        if (GetTypeError(type, parent, setting) is { } typeError)
        {
            check.AddError(typeError);
            return null;
        }

        if (LabelFieldTypes.Contains(type))
        {
            ValidateSettingKeys(check, field, type, setting);
            return null;
        }

        if (GetIdError(id, setting, declared) is { } idError)
        {
            check.AddError(idError);
            return null;
        }

        ValidateSettingKeys(check, field, type, setting);

        var element = type is ListType or ObjectType ? ReadElement(check, field, id, type, setting) : null;
        var isLink = IsLink(type, field);
        var options = type == SelectType ? ReadOptions(field) : null;

        return new CustomSectionSetting(id, type, isLink, element, options);
    }

    protected virtual void ValidateFieldKeys(CustomSectionCheck check, JObject field, string setting)
    {
        foreach (var key in field.Properties().Select(x => x.Name).Where(x => !FieldKeys.Contains(x)))
        {
            check.AddError($"{setting} has a key a custom section's setting does not take, '{Quote(key)}'");
        }

        // the designer shows these as they are
        foreach (var key in TextKeys.Where(x => field[x] is { Type: not (JTokenType.String or JTokenType.Null) }))
        {
            check.AddError($"{setting} has {(key == "info" ? "an" : "a")} {key} that is not a text");
        }
    }

    protected virtual string GetTypeError(string type, string parent, string setting)
    {
        if (type == null || !FieldTypes.Contains(type))
        {
            return $"{setting} has a type a custom section does not take, '{Quote(type)}'";
        }

        return type is ListType or ObjectType && parent != null
            ? $"{setting} is a {type} inside '{parent}', one level deeper than a custom section's settings go"
            : null;
    }

    protected virtual string GetIdError(string id, string setting, IDictionary<string, CustomSectionSetting> declared)
    {
        if (id == null || !SettingIdPattern.IsMatch(id))
        {
            return $"{setting} has no id of letters, digits and '_' that starts with a letter";
        }

        // the designer writes a value under the setting's id, next to the section's own keys
        if (SiteBuilderPageItems.ItemKeys.Contains(id, StringComparer.Ordinal))
        {
            return $"{setting} has the id of a key a section holds itself: {JoinOr(SiteBuilderPageItems.ItemKeys)}";
        }

        return declared.ContainsKey(id) ? $"{setting} has the id of another setting" : null;
    }

    private static List<string> ReadOptions(JObject field) =>
        (field["options"] as JArray)?.OfType<JObject>().Select(x => x["value"]).OfType<JValue>()
            .Select(x => Convert.ToString(x.Value, CultureInfo.InvariantCulture)).ToList();

    protected virtual void ValidateSettingKeys(CustomSectionCheck check, JObject field, string type, string setting)
    {
        if (IsSet(field, "translatable") && type is not (StringType or TextType))
        {
            check.AddError($"{setting} is translatable, which only a string or a text is");
        }

        if (field[FormatKey] != null && !IsLink(type, field))
        {
            check.AddError($"{setting} has a format other than the \"url\" of a string that holds a link");
        }

        if (IsSet(field, "multiple"))
        {
            check.AddError($"{setting} is multiple, and a custom section's setting holds one value");
        }

        ValidateOptions(check, field, type, setting);

        if (type is not (ListType or ObjectType) && field["element"] != null)
        {
            check.AddError($"{setting} has an element, which only a list or an object has");
        }

        ValidateDefault(check, field, type, setting);
    }

    protected virtual void ValidateOptions(CustomSectionCheck check, JObject field, string type, string setting)
    {
        if (type == SelectType)
        {
            if (field["options"] is not JArray { Count: > 0 } options || !options.All(IsSettingOption))
            {
                check.AddError($"{setting} is a select without a list of options, each with a value");
            }
            else if (options.Any(x => x["label"] is { Type: not (JTokenType.String or JTokenType.Null) }))
            {
                check.AddError($"{setting} has an option with a label that is not a text");
            }
        }
        else if (field["options"] != null)
        {
            check.AddError($"{setting} has options, which only a select has");
        }
    }

    protected virtual void ValidateDefault(CustomSectionCheck check, JObject field, string type, string setting)
    {
        if (field["default"] is { Type: not JTokenType.Null } value && GetDefaultError(check, type, field, value) is { } error)
        {
            check.AddError($"{setting} has a default {error}");
        }
    }

    /// <summary>The storefront leaves out a default it does not take, so a save refuses it; an empty one is none.</summary>
    /// <returns><c>null</c> for a default the setting takes.</returns>
    protected virtual string GetDefaultError(CustomSectionCheck check, string type, JObject field, JToken value)
    {
        if (value is JValue { Type: JTokenType.String } empty && ((string)empty).Length == 0)
        {
            return null;
        }

        return type switch
        {
            ListType => GetListError(check, field, value),
            ObjectType => GetObjectError(check, field, value),
            HeaderType or ParagraphType => $"that a {type} does not take: it holds no value",
            _ => GetValueError(check, type, field, value),
        };
    }

    private string GetListError(CustomSectionCheck check, JObject field, JToken value)
    {
        var elements = GetElements(field);

        return value is JArray rows && rows.All(x => x is JObject)
            ? rows.Select((row, i) => GetRowError(check, elements, (JObject)row) is { } error ? string.Create(CultureInfo.InvariantCulture, $"whose row {i + 1} holds {error}") : null)
                .FirstOrDefault(x => x != null)
            : "that is not a list of rows";
    }

    private string GetObjectError(CustomSectionCheck check, JObject field, JToken value)
    {
        if (value is not JObject row)
        {
            return "that is not an object of its fields";
        }

        return GetRowError(check, GetElements(field), row) is { } error ? $"that holds {error}" : null;
    }

    private string GetValueError(CustomSectionCheck check, string type, JObject field, JToken value) => type switch
    {
        NumberType or SliderType => GetNumberError(field, value),
        CheckboxType => value.Type == JTokenType.Boolean ? null : "that is not true or false",
        ColorTokenType => GetColorTokenError(check, value),
        SelectType => GetOptionError(check, field, value),
        _ => GetTextError(type, field, value),
    };

    private static string GetColorTokenError(CustomSectionCheck check, JToken value) =>
        check.Kit?.PaletteTokens is not { Count: > 0 } tokens || value is JValue { Type: JTokenType.String } token && tokens.Contains((string)token)
            ? null
            : "that is not one of the palette's tokens";

    private static string GetOptionError(CustomSectionCheck check, JObject field, JToken value)
    {
        if (!check.SelectOptions.TryGetValue(field, out var options))
        {
            options = new HashSet<JToken>((field["options"] as JArray)?.OfType<JObject>().Select(x => x["value"]).Where(x => x != null) ?? [], JToken.EqualityComparer);
            check.SelectOptions[field] = options;
        }

        return options.Contains(value) ? null : "that is not one of its options";
    }

    private string GetTextError(string type, JObject field, JToken value)
    {
        // a translatable value is a text per language
        var isTranslatable = IsSet(field, "translatable");
        List<string> texts = value switch
        {
            JValue { Type: JTokenType.String } text => [(string)text],
            JObject translations when isTranslatable && ThemeTranslations.IsTranslation(translations) => [.. translations.Properties().Select(x => (string)x.Value)],
            _ => null,
        };
        if (texts == null)
        {
            return isTranslatable ? "that is not a text, or a text per language" : "that is not a text";
        }

        return GetTextFormatError(type, field, texts);
    }

    private string GetTextFormatError(string type, JObject field, List<string> texts)
    {
        if (IsLink(type, field) && texts.Exists(x => x.Length > 0 && !urlPolicy.IsLink(x, CustomSectionTemplateValidator.LinkSchemes)))
        {
            return $"that is not a path starting with a single '/', an anchor such as #reviews, or an {JoinOr(CustomSectionTemplateValidator.LinkSchemes)} URL";
        }

        if (type == CalendarType && texts.Exists(x => x.Length > 0 && !CustomSectionTemplateValidator.IsIsoDate(x)))
        {
            return "that is not an ISO 8601 date, or a date and a time with its offset, such as 2026-12-31 or 2026-12-31T23:59:59Z";
        }

        return type == ImagesType && texts.Exists(x => x.Length > 0 && !urlPolicy.IsAssetUrl(x))
            ? "that is not a URL of the store's asset library"
            : null;
    }

    // each field of a list's row or of an object by its own rule, in the order of the fields; a row reads only its own values, as a list may have
    // many fields and many rows
    private string GetRowError(CustomSectionCheck check, Dictionary<string, (int Index, JObject Field)> elements, JObject row) =>
        row.Properties()
            .Select(x => (x.Name, x.Value, Element: elements.GetValueOrDefault(x.Name)))
            .Where(x => x.Element.Field != null && x.Value.Type != JTokenType.Null)
            .OrderBy(x => x.Element.Index)
            .Select(x => GetDefaultError(check, ThemeJson.GetString(x.Element.Field, "type"), x.Element.Field, x.Value) is { } error ? $"'{x.Name}' {error}" : null)
            .FirstOrDefault(x => x != null);

    // by id, read once for all the rows of a list; a list or an object among them is refused where it is declared, so a row does not read its fields
    private static Dictionary<string, (int Index, JObject Field)> GetElements(JObject field) =>
        ((field["element"] as JArray)?.OfType<JObject>() ?? [])
            .Select((x, i) => (Id: ThemeJson.GetString(x, "id"), Index: i, Field: x))
            .Where(x => x.Id != null && ThemeJson.GetString(x.Field, "type") is not (ListType or ObjectType))
            .DistinctBy(x => x.Id, StringComparer.Ordinal)
            .ToDictionary(x => x.Id, x => (x.Index, x.Field), StringComparer.Ordinal);

    private static string GetNumberError(JObject field, JToken value)
    {
        if (GetNumber(value) is not { } number || !double.IsFinite(number))
        {
            return "that is not a number";
        }

        if (GetNumber(field["min"]) is { } min && number < min)
        {
            return "that is less than its min";
        }

        return GetNumber(field["max"]) is { } max && number > max ? "that is more than its max" : null;
    }

    // a big integer as the storefront reads it: a double
    private static double? GetNumber(JToken token) => (token as JValue)?.Value switch
    {
        long number => number,
        double number => number,
        decimal number => (double)number,
        BigInteger number => (double)number,
        _ => null,
    };

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
        JValue { Type: JTokenType.String } text => IsNameText((string)text),
        JObject translations => ThemeTranslations.IsTranslation(translations) && translations.Properties().All(x => IsNameText((string)x.Value)),
        _ => false,
    };

    private static bool IsNameText(string text) => !string.IsNullOrWhiteSpace(text) && text.Length <= MaxNameLength;

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

    private static string FormatSize(long size) => string.Create(CultureInfo.InvariantCulture, $"{(double)size / BytesPerKilobyte:0.#} KB");
}
