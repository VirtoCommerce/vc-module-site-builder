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

    private static readonly HashSet<string> FieldKeys = new(StringComparer.Ordinal) { "type", "options", "min", "max", "description" };

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
            return new StorefrontKit(catalog != null, Tags: null, Behaviors: null);
        }

        var tags = ReadEntries(kit["tags"], TagPrefix, x => ReadFields(x["attributes"]));
        // a stored catalog may name a behavior its upload would refuse now
        var behaviors = ReadEntries(kit["behaviors"], string.Empty,
                x => new KitBehavior(x["value"] is JObject value ? ReadField(value) : null, ReadFields(x["options"])))
            .Where(x => !IsReservedBehavior(x.Key))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

        return new StorefrontKit(true, tags, behaviors);
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
            ?? GetEntriesError(entries["behaviors"], "behaviors", GetBehaviorNameError, GetBehaviorError);
    }

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
        ThemeContentReader.GetString(field, "type"),
        (field["options"] as JArray)?
            .Select(GetOptionValue)
            .Where(x => x != null)
            .Select(x => Convert.ToString(x.Value, CultureInfo.InvariantCulture))
            .ToList(),
        GetNumber(field, "min"),
        GetNumber(field, "max"));

    private static decimal? GetNumber(JObject item, string name) =>
        item[name] is JValue { Type: JTokenType.Integer or JTokenType.Float } value ? value.Value<decimal>() : null;
}
