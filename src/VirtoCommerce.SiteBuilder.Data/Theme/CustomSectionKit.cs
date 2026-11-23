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

    /// <summary>The catalog's palette tokens, beside its kit.</summary>
    public const string PaletteTokensKey = "paletteTokens";

    private const string TemplatesKey = "templates";
    public const string DesignKey = "design";

    public const int MaxGuideLength = 16 * 1024;

    public const int MaxIcons = 10_000;
    public const int MaxIconLength = 100;
    public const int MaxDescriptionLength = 1024;

    // what any page-saver uploads reaches every template check and, as text, the agent: each count and length is bounded
    public const int MaxTags = 256;
    public const int MaxBehaviors = 256;

    /// <summary>Of a tag, its attributes; of a behavior, its options and its value together.</summary>
    public const int MaxFields = 64;

    /// <summary>Of a select.</summary>
    public const int MaxOptions = 256;

    /// <summary>Of an option's value or label.</summary>
    public const int MaxOptionLength = 256;

    /// <summary>The storefront's render budget: a kit tag's cost, the most elements one instance renders, is at most it.</summary>
    public const int RenderBudget = 5000;

    /// <summary>The most characters of bound values and rich text the storefront renders in a section.</summary>
    public const int MaxRenderedText = 262_144;
    public const int MaxWrites = 32;
    public const int MaxWriteLength = 128;

    /// <summary>Of a tag, a behavior, an attribute or an option; a behavior is found by the prefixes of an attribute's name up to it.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Of each map of the design, its breakpoints and its tokens: ten times what the storefront sends, rounded up.</summary>
    public const int MaxDesignEntries = 1024;

    /// <summary>Of each name and text of the design.</summary>
    public const int MaxDesignTextLength = 1024;

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
        "style", "id", "is", "class", "srcdoc", "ping", "srcset", "poster", "action", "command", "commandfor", "interestfor",
    };

    /// <summary>The starts of the names no behavior takes, after data-: the attributes the storefront writes itself.</summary>
    public static IReadOnlyList<string> ReservedBehaviorPrefixes { get; } = ["sb-", "pb-"];

    /// <summary>Checked as links, whatever type the catalog declares.</summary>
    public static IReadOnlySet<string> LinkAttributes { get; } = new HashSet<string>(StringComparer.Ordinal) { "to", "href" };

    /// <summary>Checked as images, whatever type the catalog declares.</summary>
    public static IReadOnlySet<string> ImageAttributes { get; } = new HashSet<string>(StringComparer.Ordinal) { "src" };

    private static readonly HashSet<string> BindingNames = new(StringComparer.Ordinal) { "each", "if", "html" };

    private const string DescriptionKey = "description";
    private const string CostKey = "cost";
    private const string HasSideEffectsKey = "hasSideEffects";
    private const string TextAttributesKey = "textAttributes";

    /// <summary>The keys a kit field takes; the section kit passes the agent these only.</summary>
    public static IReadOnlySet<string> FieldKeys { get; } = new HashSet<string>(StringComparer.Ordinal) { "type", "options", "optionsFrom", "min", "max", DescriptionKey };

    /// <summary>The keys a kit tag takes.</summary>
    public static IReadOnlySet<string> TagKeys { get; } = new HashSet<string>(StringComparer.Ordinal) { "attributes", DescriptionKey, CostKey, HasSideEffectsKey };

    /// <summary>The keys a behavior takes.</summary>
    public static IReadOnlySet<string> BehaviorKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "value", "options", "writes", "repeatsChildren", TextAttributesKey, DescriptionKey,
    };

    /// <summary>The keys an option written as an object takes.</summary>
    public static IReadOnlySet<string> OptionKeys { get; } = new HashSet<string>(StringComparer.Ordinal) { "value", "label" };

    private static readonly HashSet<string> DesignKeys = new(StringComparer.Ordinal) { "breakpoints", "containerMaxWidth", "tokens", "containerName" };

    private static readonly HashSet<string> DesignTokenKeys = new(StringComparer.Ordinal) { "fallback", "description" };

    private const string DataPrefix = "data-";
    private const double MaxDecimal = 7.9e28;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex TagNamePattern = new("^vc-[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.None, RegexTimeout);
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.None, RegexTimeout);

    /// <summary>
    /// As written, or as the element gets it: the storefront passes the name camelCased, and one the component does not declare falls through
    /// to its element, which lowercases it, so o-nclick is onclick there, and h-ref an href past the link rule.
    /// </summary>
    public static bool IsDeniedAttribute(string name)
    {
        if (IsDenied(name))
        {
            return true;
        }

        var written = name.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

        return written != name.ToLowerInvariant() && (IsDenied(written) || LinkAttributes.Contains(written) || ImageAttributes.Contains(written));
    }

    private static bool IsDenied(string name) =>
        DeniedAttributePrefixes.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase)) || DeniedAttributes.Contains(name.ToLowerInvariant());

    public static bool IsReservedBehavior(string name) =>
        BindingNames.Contains(name) || ReservedBehaviorPrefixes.Any(x => name.StartsWith(x, StringComparison.Ordinal) || name == x.TrimEnd('-'));

    /// <summary>A data- attribute the storefront writes itself, or a binding's.</summary>
    public static bool IsReservedDataAttribute(string name) =>
        name.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase) && IsReservedBehavior(name[DataPrefix.Length..].ToLowerInvariant());

    public static StorefrontKit Read(JObject catalog)
    {
        var paletteTokens = (catalog?[PaletteTokensKey] as JObject)?.Properties()
            .Where(x => x.Name is "light" or "dark")
            .SelectMany(x => (x.Value as JArray)?.OfType<JValue>().Where(v => v.Type == JTokenType.String).Select(v => (string)v) ?? [])
            .ToFrozenSet(StringComparer.Ordinal);
        var templateKeys = (catalog?[TemplatesKey] as JObject)?.Properties().Select(x => x.Name).ToFrozenSet(StringComparer.Ordinal);
        if (catalog?[KitKey] is not JObject kit)
        {
            return new StorefrontKit(catalog != null, Tags: null, Behaviors: null, Icons: null) { PaletteTokens = paletteTokens, TemplateKeys = templateKeys };
        }

        var tags = ReadEntries(kit["tags"], TagPrefix, x => ReadFields(x["attributes"]));
        // a stored catalog may name a behavior its upload would refuse now
        var behaviors = ReadEntries(kit["behaviors"], string.Empty,
                x => new KitBehavior(x["value"] is JObject value ? ReadField(value) : null, ReadFields(x["options"]),
                    x["repeatsChildren"] is JValue { Type: JTokenType.Boolean } repeats && (bool)repeats)
                {
                    TextAttributes = (x[TextAttributesKey] as JArray)?.OfType<JValue>().Where(v => v.Type == JTokenType.String).Select(v => (string)v)
                        .ToFrozenSet(StringComparer.Ordinal),
                })
            .Where(x => !IsReservedBehavior(x.Key))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var icons = (kit[IconsKey] as JArray)?
            .OfType<JValue>()
            .Where(x => x.Type == JTokenType.String)
            .Select(x => (string)x)
            .ToFrozenSet(StringComparer.Ordinal);

        var sideEffectTags = (kit["tags"] as JObject)?.Properties()
            .Where(x => x.Value is JObject tag && tag[HasSideEffectsKey] is JValue { Type: JTokenType.Boolean } sideEffects && (bool)sideEffects)
            .Select(x => x.Name)
            .ToFrozenSet(StringComparer.Ordinal);

        // a stored catalog may hold a cost its upload would refuse now: it counts as none
        var tagCosts = (kit["tags"] as JObject)?.Properties()
            .Where(x => x.Value is JObject tag && tag[CostKey] is JValue { Type: JTokenType.Integer, Value: >= 1L and <= (long)RenderBudget })
            .ToFrozenDictionary(x => x.Name, x => (int)(long)((JValue)x.Value[CostKey]).Value, StringComparer.Ordinal);

        return new StorefrontKit(true, tags, behaviors, icons)
        {
            PaletteTokens = paletteTokens, TemplateKeys = templateKeys, SideEffectTags = sideEffectTags, TagCosts = tagCosts,
        };
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

        return GetEntriesError(entries["tags"], "tags", MaxTags, x => TagNamePattern.IsMatch(x) ? null : "does not start with vc- and lowercase words joined by '-'", (_, tag) => GetTagError(tag))
            ?? GetEntriesError(entries["behaviors"], "behaviors", MaxBehaviors, GetBehaviorNameError, GetBehaviorError)
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
            IsMapOrNone(facts["breakpoints"], IsDesignText) &&
            IsMapOrNone(facts["tokens"], x => x is JObject token && token.Properties().All(p => DesignTokenKeys.Contains(p.Name) && IsDesignText(p.Value)));

        return isRight ? null : string.Create(CultureInfo.InvariantCulture, $"has a design whose breakpoints, containerMaxWidth, tokens or containerName are " +
            $"not texts as the storefront sends them, at most {MaxDesignEntries} entries each of at most {MaxDesignTextLength} characters");
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

    private static bool IsTextOrNone(JToken token) => token is null || token.Type == JTokenType.Null || IsDesignText(token);

    private static bool IsDesignText(JToken token) => token is JValue { Type: JTokenType.String } text && ((string)text).Length <= MaxDesignTextLength;

    private static bool IsMapOrNone(JToken token, Func<JToken, bool> isValue) =>
        token is null || token.Type == JTokenType.Null ||
        (token is JObject map && map.Count <= MaxDesignEntries && map.Properties().All(x => x.Name.Length <= MaxDesignTextLength && isValue(x.Value)));

    private static string GetNameLengthError(string name) =>
        name.Length > MaxNameLength ? string.Create(CultureInfo.InvariantCulture, $"is longer than {MaxNameLength} characters") : null;

    /// <param name="getEntryError">With the entry's name.</param>
    private static string GetEntriesError(JToken entries, string name, int max, Func<string, string> getNameError, Func<string, JObject, string> getEntryError)
    {
        if (entries is null || entries.Type == JTokenType.Null)
        {
            return null;
        }

        if (entries is not JObject map)
        {
            return $"has {name} that are not an object";
        }

        if (map.Count > max)
        {
            return string.Create(CultureInfo.InvariantCulture, $"has more than {max} {name}");
        }

        foreach (var entry in map.Properties())
        {
            var error = GetNameLengthError(entry.Name) ?? getNameError(entry.Name) ?? (entry.Value is JObject definition ? getEntryError(entry.Name, definition) : "is not an object");
            if (error != null)
            {
                return $"has the {name[..^1]} '{entry.Name}', which {error}";
            }
        }

        return null;
    }

    private static string GetTagError(JObject tag) =>
        GetKeysError(tag, TagKeys)
        ?? (tag[CostKey] is null or { Type: JTokenType.Null } or JValue { Type: JTokenType.Integer, Value: >= 1L and <= (long)RenderBudget } ? null
            : string.Create(CultureInfo.InvariantCulture, $"has a cost that is not a whole number from 1 to {RenderBudget}"))
        ?? (tag[HasSideEffectsKey] is null or { Type: JTokenType.Null or JTokenType.Boolean } ? null : "has a hasSideEffects that is not true or false")
        ?? ((tag["attributes"] as JObject)?.Count > MaxFields ? string.Create(CultureInfo.InvariantCulture, $"has more than {MaxFields} attributes") : null)
        ?? GetFieldsError(tag["attributes"], "attribute",
            x => IsDeniedAttribute(x) ? "no kit tag takes" : IsReservedDataAttribute(x) ? "the storefront writes itself" : null);

    private static string GetBehaviorNameError(string name) =>
        !NamePattern.IsMatch(name) ? "is not lowercase words joined by '-'"
        : IsReservedBehavior(name) ? "is the name of a binding, or is sb or pb, or starts with sb- or pb-, as the storefront's own attributes do"
        : null;

    private static string GetKeysError(JObject entry, IReadOnlySet<string> keys)
    {
        if (entry.Properties().FirstOrDefault(x => !keys.Contains(x.Name)) is { } unknown)
        {
            return $"has a key it does not take, '{unknown.Name}'";
        }

        return GetDescriptionError(entry);
    }

    private static string GetDescriptionError(JObject entry) => entry[DescriptionKey] switch
    {
        null or { Type: JTokenType.Null } => null,
        JValue { Type: JTokenType.String } text when ((string)text).Length <= MaxDescriptionLength => null,
        _ => string.Create(CultureInfo.InvariantCulture, $"has a description that is not a text of at most {MaxDescriptionLength} characters"),
    };

    private static string GetBehaviorError(string name, JObject behavior)
    {
        if (GetKeysError(behavior, BehaviorKeys) is { } keysError)
        {
            return keysError;
        }

        if (behavior["value"] is { Type: not JTokenType.Null } value && GetFieldError(value) is { } valueError)
        {
            return $"has a value that {valueError}";
        }

        if ((behavior["options"] as JObject)?.Count + (behavior["value"] is { Type: not JTokenType.Null } ? 1 : 0) > MaxFields)
        {
            return string.Create(CultureInfo.InvariantCulture, $"has more than {MaxFields} fields, its options and its value together");
        }

        if (behavior["writes"] is { Type: not JTokenType.Null } writes &&
            !(writes is JArray list && list.Count <= MaxWrites && list.All(x => x is JValue { Type: JTokenType.String } write && ((string)write).Length <= MaxWriteLength)))
        {
            return string.Create(CultureInfo.InvariantCulture, $"has writes that are not a list of at most {MaxWrites} names, each of at most {MaxWriteLength} characters");
        }

        if (behavior["repeatsChildren"] is { Type: not (JTokenType.Null or JTokenType.Boolean) })
        {
            return "has a repeatsChildren that is not true or false";
        }

        // the storefront leaves out the template's text in an element with one, so it is the behavior's own attribute or an option's
        var ownAttributes = ((behavior["options"] as JObject)?.Properties().Select(x => $"{DataPrefix}{name}-{x.Name}") ?? []).Prepend(DataPrefix + name).ToList();
        if (behavior[TextAttributesKey] is { Type: not JTokenType.Null } textAttributes &&
            !(textAttributes is JArray attributes && attributes.All(x => x is JValue { Type: JTokenType.String } attribute && ownAttributes.Contains((string)attribute, StringComparer.Ordinal))))
        {
            return "has textAttributes that are not a list of its own data-{name} attribute and its options'";
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
            var error = GetNameLengthError(field.Name) ??
                (NamePattern.IsMatch(field.Name) ? getNameError(field.Name) : "is not lowercase words joined by '-'") ?? GetFieldError(field.Value);
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

        if (field["options"] is JArray { Count: > MaxOptions })
        {
            return string.Create(CultureInfo.InvariantCulture, $"has more than {MaxOptions} options");
        }

        if ((field["options"] as JArray)?.OfType<JObject>().SelectMany(x => x.Properties()).FirstOrDefault(x => !OptionKeys.Contains(x.Name)) is { } unknownKey)
        {
            return $"has an option with a key it does not take, '{unknownKey.Name}'";
        }

        if (field["options"] is { Type: not JTokenType.Null } options && !(options is JArray list && list.All(IsOption)))
        {
            return string.Create(CultureInfo.InvariantCulture, $"has options that are not a list of values, or of {{ value, label }}, each text of at most {MaxOptionLength} characters");
        }

        if (field["optionsFrom"] is { Type: not JTokenType.Null } optionsFrom &&
            !((string)type == SelectType && optionsFrom is JValue { Type: JTokenType.String } source && (string)source == IconsKey))
        {
            return $"has an optionsFrom other than \"{IconsKey}\" of a select";
        }

        // no literal could be one of its values
        if ((string)type == SelectType && field["options"] is null or { Type: JTokenType.Null } && field["optionsFrom"] is null or { Type: JTokenType.Null })
        {
            return "is a select with neither options nor optionsFrom";
        }

        if (field["min"] is { Type: not JTokenType.Null } min && GetNumber(min) == null ||
            field["max"] is { Type: not JTokenType.Null } max && GetNumber(max) == null)
        {
            return "has a min or a max that is not a number within range";
        }

        return GetDescriptionError(field);
    }

    private static bool IsOption(JToken option) =>
        GetOptionValue(option) is { } value && Convert.ToString(value.Value, CultureInfo.InvariantCulture).Length <= MaxOptionLength &&
        (option is not JObject item || item["label"] is null or { Type: JTokenType.Null } || item["label"] is JValue { Type: JTokenType.String } label && ((string)label).Length <= MaxOptionLength);

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
        GetNumber(field["min"]),
        GetNumber(field["max"]),
        ThemeJson.GetString(field, "optionsFrom"));

    // a number a decimal holds; none for a big integer or a float out of its range, as a catalog stored earlier may hold
    private static decimal? GetNumber(JToken token) => (token as JValue)?.Value switch
    {
        long number => number,
        decimal number => number,
        double number when double.IsFinite(number) && Math.Abs(number) < MaxDecimal => (decimal)number,
        _ => null,
    };
}
