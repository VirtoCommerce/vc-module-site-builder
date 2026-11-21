using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>
/// The kit the storefront announces in its catalog: the kit tags and the behaviors a custom section may use. Anyone who may save a page
/// uploads a catalog, so the kit is not trusted further than its shape and the attributes no kit tag may take.
/// </summary>
public static class CustomSectionKit
{
    /// <summary>The key of the kit in the storefront's schema catalog.</summary>
    public const string KitKey = "kit";

    public const string StringType = "string";
    public const string NumberType = "number";
    public const string BooleanType = "boolean";
    public const string SelectType = "select";
    public const string DateType = "date";
    public const string LinkType = "link";

    public const string TagPrefix = "vc-";

    /// <summary>The key of the storefront's icon names in the kit, and the <c>optionsFrom</c> of a select that takes one of them.</summary>
    public const string IconsKey = "icons";

    public const string GuideKey = "guide";
    public const string DesignKey = "design";

    public const int MaxGuideLength = 16 * 1024;

    public const int MaxIcons = 10_000;
    public const int MaxIconLength = 100;

    public static IReadOnlySet<string> FieldTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        StringType, NumberType, BooleanType, SelectType, DateType, LinkType,
    };

    /// <summary>The starts of the names of attributes no kit tag takes: those that run script, reach a form or open a popover.</summary>
    public static IReadOnlyList<string> DeniedAttributePrefixes { get; } = ["on", "form", "popover"];

    /// <summary>
    /// Besides <see cref="DeniedAttributePrefixes"/>: they style or name the element, hold a document, or load a URL past the link and image rules.
    /// </summary>
    public static IReadOnlySet<string> DeniedAttributes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "style", "id", "is", "class", "srcdoc", "ping", "srcset", "poster", "action",
    };

    /// <summary>The starts of the names no behavior takes, after data-: the attributes the storefront writes itself.</summary>
    public static IReadOnlyList<string> ReservedBehaviorPrefixes { get; } = ["sb-", "pb-"];

    /// <summary>Checked as links, whatever type the catalog declares.</summary>
    public static IReadOnlySet<string> LinkAttributes { get; } = new HashSet<string>(StringComparer.Ordinal) { "to", "href", "src" };

    private static readonly HashSet<string> BindingNames = new(StringComparer.Ordinal) { "each", "if", "html" };

    private static readonly HashSet<string> FieldKeys = new(StringComparer.Ordinal) { "type", "options", "optionsFrom", "min", "max", "description" };

    private static readonly HashSet<string> DesignKeys = new(StringComparer.Ordinal) { "breakpoints", "containerMaxWidth", "tokens", "containerName" };

    private static readonly HashSet<string> DesignTokenKeys = new(StringComparer.Ordinal) { "fallback", "description" };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex TagNamePattern = new("^vc-[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.None, RegexTimeout);
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.None, RegexTimeout);

    public static bool IsDeniedAttribute(string name) =>
        DeniedAttributePrefixes.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase)) || DeniedAttributes.Contains(name.ToLowerInvariant());

    public static bool IsReservedBehavior(string name) =>
        BindingNames.Contains(name) || ReservedBehaviorPrefixes.Any(x => name.StartsWith(x, StringComparison.Ordinal));

    public static StorefrontKit Read(JObject catalog)
    {
        if (catalog?[KitKey] is not JObject kit)
        {
            return new StorefrontKit(catalog != null, Tags: null, Behaviors: null, Icons: null);
        }

        var tags = ReadEntries(kit["tags"], TagPrefix, x => ReadFields(x["attributes"]));
        // a stored catalog may name a behavior its upload would refuse now
        var behaviors = ReadEntries(kit["behaviors"], string.Empty,
                x => new KitBehavior(x["value"] is JObject value ? ReadField(value) : null, ReadFields(x["options"]),
                    x["repeatsChildren"] is JValue { Type: JTokenType.Boolean } repeats && (bool)repeats))
            .Where(x => !IsReservedBehavior(x.Key))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var icons = (kit[IconsKey] as JArray)?
            .OfType<JValue>()
            .Where(x => x.Type == JTokenType.String)
            .Select(x => (string)x)
            .ToFrozenSet(StringComparer.Ordinal);

        return new StorefrontKit(true, tags, behaviors, icons);
    }

    /// <returns>What is wrong with the kit's shape, naming where; <c>null</c> for a kit that is right or for none.</returns>
    public static string GetShapeError(JToken kit)
    {
        if (kit is null || kit.Type == JTokenType.Null)
        {
            return null;
        }

        if (kit is not JObject entries)
        {
            return "is not an object";
        }

        return GetEntriesError(entries["tags"], "tags", x => TagNamePattern.IsMatch(x) ? null : "does not start with vc- and lowercase words joined by '-'", GetTagError)
            ?? GetEntriesError(entries["behaviors"], "behaviors", GetBehaviorNameError, GetBehaviorError)
            ?? GetGuideError(entries[GuideKey])
            ?? GetDesignError(entries[DesignKey])
            ?? GetIconsError(entries[IconsKey]);
    }

    private static string GetGuideError(JToken guide) => guide switch
    {
        null or { Type: JTokenType.Null } => null,
        JValue { Type: JTokenType.String } text when ((string)text).Length <= MaxGuideLength => null,
        JValue { Type: JTokenType.String } => string.Create(CultureInfo.InvariantCulture, $"has a guide longer than {MaxGuideLength} characters"),
        _ => "has a guide that is not a text",
    };

    private static string GetDesignError(JToken design)
    {
        if (design is null || design.Type == JTokenType.Null)
        {
            return null;
        }

        if (design is not JObject facts)
        {
            return "has a design that is not an object";
        }

        if (facts.Properties().FirstOrDefault(x => !DesignKeys.Contains(x.Name)) is { } unknown)
        {
            return $"has a design with a key it does not have, '{unknown.Name}'";
        }

        var isRight = IsTextOrNone(facts["containerMaxWidth"]) && IsTextOrNone(facts["containerName"]) &&
            IsMapOrNone(facts["breakpoints"], x => x.Type == JTokenType.String) &&
            IsMapOrNone(facts["tokens"], x => x is JObject token && token.Properties().All(p => DesignTokenKeys.Contains(p.Name) && p.Value.Type == JTokenType.String));

        return isRight ? null : "has a design whose breakpoints, containerMaxWidth, tokens or containerName are not texts as the storefront sends them";
    }

    private static string GetIconsError(JToken icons)
    {
        if (icons is null || icons.Type == JTokenType.Null)
        {
            return null;
        }

        if (icons is not JArray names || names.Count > MaxIcons ||
            !names.All(x => x is JValue { Type: JTokenType.String } name && ((string)name).Length is > 0 and <= MaxIconLength))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"has icons that are not a list of at most {MaxIcons} names, each of 1 to {MaxIconLength} characters");
        }

        return null;
    }

    private static bool IsTextOrNone(JToken token) => token is null || token.Type is JTokenType.Null or JTokenType.String;

    private static bool IsMapOrNone(JToken token, Func<JToken, bool> isValue) =>
        token is null || token.Type == JTokenType.Null || (token is JObject map && map.Properties().All(x => isValue(x.Value)));

    private static string GetEntriesError(JToken entries, string name, Func<string, string> getNameError, Func<JObject, string> getEntryError)
    {
        if (entries is null || entries.Type == JTokenType.Null)
        {
            return null;
        }

        if (entries is not JObject map)
        {
            return $"has {name} that are not an object";
        }

        foreach (var entry in map.Properties())
        {
            var error = getNameError(entry.Name) ?? (entry.Value is JObject definition ? getEntryError(definition) : "is not an object");
            if (error != null)
            {
                return $"has the {name[..^1]} '{entry.Name}', which {error}";
            }
        }

        return null;
    }

    private static string GetTagError(JObject tag) =>
        GetFieldsError(tag["attributes"], "attribute", x => IsDeniedAttribute(x) ? "no kit tag takes" : null);

    private static string GetBehaviorNameError(string name) =>
        !NamePattern.IsMatch(name) ? "is not lowercase words joined by '-'"
        : IsReservedBehavior(name) ? "is the name of a binding, or starts with sb- or pb-, as the storefront's own attributes do"
        : null;

    private static string GetBehaviorError(JObject behavior)
    {
        if (behavior["value"] is { Type: not JTokenType.Null } value && GetFieldError(value) is { } valueError)
        {
            return $"has a value that {valueError}";
        }

        if (behavior["writes"] is { Type: not JTokenType.Null } writes && !(writes is JArray list && list.All(x => x.Type == JTokenType.String)))
        {
            return "has writes that are not a list of names";
        }

        if (behavior["repeatsChildren"] is { Type: not (JTokenType.Null or JTokenType.Boolean) })
        {
            return "has a repeatsChildren that is not true or false";
        }

        return GetFieldsError(behavior["options"], "option", _ => null);
    }

    private static string GetFieldsError(JToken fields, string name, Func<string, string> getNameError)
    {
        if (fields is null || fields.Type == JTokenType.Null)
        {
            return null;
        }

        if (fields is not JObject map)
        {
            return $"has {name}s that are not an object";
        }

        foreach (var field in map.Properties())
        {
            var error = (NamePattern.IsMatch(field.Name) ? getNameError(field.Name) : "is not lowercase words joined by '-'") ?? GetFieldError(field.Value);
            if (error != null)
            {
                return $"has the {name} '{field.Name}', which {error}";
            }
        }

        return null;
    }

    private static string GetFieldError(JToken token)
    {
        if (token is not JObject field)
        {
            return "is not an object";
        }

        if (field.Properties().FirstOrDefault(x => !FieldKeys.Contains(x.Name)) is { } unknown)
        {
            return $"has a key a kit field does not have, '{unknown.Name}'";
        }

        if (field["type"] is not JValue { Type: JTokenType.String } type || !FieldTypes.Contains((string)type))
        {
            return "has no type of string, number, boolean, select, date or link";
        }

        if (field["options"] is { Type: not JTokenType.Null } options && !(options is JArray list && list.All(IsOption)))
        {
            return "has options that are not a list of values";
        }

        if (field["optionsFrom"] is { Type: not JTokenType.Null } optionsFrom &&
            !((string)type == SelectType && optionsFrom is JValue { Type: JTokenType.String } source && (string)source == IconsKey))
        {
            return $"has an optionsFrom other than \"{IconsKey}\" of a select";
        }

        return field["min"] is { Type: not (JTokenType.Null or JTokenType.Integer or JTokenType.Float) } ||
            field["max"] is { Type: not (JTokenType.Null or JTokenType.Integer or JTokenType.Float) } ||
            field["description"] is { Type: not (JTokenType.Null or JTokenType.String) }
            ? "has a min, a max or a description of another kind"
            : null;
    }

    private static bool IsOption(JToken option) => GetOptionValue(option) != null;

    // a value, or an object with its value and its label
    private static JValue GetOptionValue(JToken option) =>
        (option is JObject item ? item["value"] : option) as JValue is { Type: JTokenType.String or JTokenType.Integer or JTokenType.Float } value ? value : null;

    private static Dictionary<string, T> ReadEntries<T>(JToken entries, string prefix, Func<JObject, T> read) =>
        (entries as JObject)?.Properties()
            .Where(x => x.Name.StartsWith(prefix, StringComparison.Ordinal) && x.Name.Length > prefix.Length && x.Value is JObject)
            .ToDictionary(x => x.Name, x => read((JObject)x.Value), StringComparer.Ordinal)
        ?? new Dictionary<string, T>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, KitField> ReadFields(JToken fields) =>
        (fields as JObject)?.Properties()
            .Where(x => x.Value is JObject)
            .ToDictionary(x => x.Name, x => ReadField((JObject)x.Value), StringComparer.Ordinal)
        ?? new Dictionary<string, KitField>(StringComparer.Ordinal);

    private static KitField ReadField(JObject field) => new(
        ThemeJson.GetString(field, "type"),
        (field["options"] as JArray)?
            .Select(GetOptionValue)
            .Where(x => x != null)
            .Select(x => Convert.ToString(x.Value, CultureInfo.InvariantCulture))
            .ToList(),
        GetNumber(field, "min"),
        GetNumber(field, "max"),
        ThemeJson.GetString(field, "optionsFrom"));

    private static decimal? GetNumber(JObject item, string name) =>
        item[name] is JValue { Type: JTokenType.Integer or JTokenType.Float } value ? value.Value<decimal>() : null;
}
