using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>
/// The kit the storefront announces in its catalog: the kit tags and the behaviors a custom section may use. Anyone who may save a page
/// uploads a catalog, so the kit is not trusted further than its shape and the attributes no kit tag may take.
/// </summary>
public static class CustomSectionKit
{
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

    // the binding attributes of a template, which no behavior is named as
    private static readonly HashSet<string> BindingNames = new(StringComparer.Ordinal) { "each", "if", "html" };

    private static readonly HashSet<string> FieldKeys = new(StringComparer.Ordinal) { "type", "options", "min", "max", "description" };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex TagNamePattern = new("^vc-[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.None, RegexTimeout);
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.None, RegexTimeout);

    /// <summary>An attribute that runs script, names the element or reaches a form, which no kit tag takes whatever the catalog declares.</summary>
    public static bool IsDeniedAttribute(string name) =>
        name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("form", StringComparison.OrdinalIgnoreCase) ||
        name.ToLowerInvariant() is "style" or "id" or "is" or "class" or "srcdoc";

    /// <summary>An attribute a component reads as a link, which follows the link rule whatever type the catalog declares.</summary>
    public static bool IsLinkAttribute(string name) => name.ToLowerInvariant() is "to" or "href" or "src";

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
        !NamePattern.IsMatch(name) ? "is not lowercase words joined by '-'" : BindingNames.Contains(name) ? "is the name of a binding" : null;

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

    // a value, or an object with its value and its label
    private static bool IsOption(JToken option) =>
        option is JValue { Type: JTokenType.String or JTokenType.Integer or JTokenType.Float } ||
        (option is JObject item && item["value"] is JValue { Type: JTokenType.String or JTokenType.Integer or JTokenType.Float });
}
