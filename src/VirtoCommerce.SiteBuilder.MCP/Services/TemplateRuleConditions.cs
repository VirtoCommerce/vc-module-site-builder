using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

public sealed record PageRuleSubject(string Permalink, string Language, JsonObject Settings, string ObjectType);

// Mirrors the storefront's built-in conditions, which have no server twin: change the two together.
public static class TemplateRuleConditions
{
    public const string PagesObjectType = "Pages";
    public const string ContentFileObjectType = "ContentFile";

    private static readonly Dictionary<string, (string[] PageTypes, string Shape)> BuiltIn = new(StringComparer.Ordinal)
    {
        ["inCategory"] = (["product", "category"], "a list of category ids"),
        ["productType"] = (["product"], "a product type"),
        ["property"] = (["product"], "{ \"name\": property name, \"equals\": value }"),
        ["path"] = (["page"], "a page path; * matches any characters"),
        ["language"] = (["page"], "a language, such as fr-FR or fr"),
        ["source"] = (["page"], $"'{PagesObjectType}' (pages of the database) or '{ContentFileObjectType}' (pages kept as files)"),
        ["setting"] = (["page"], "{ \"name\": page setting, \"equals\": value }"),
    };

    public static IEnumerable<(string Name, IReadOnlyList<string> PageTypes, string Shape)> All =>
        BuiltIn.Select(x => (x.Key, (IReadOnlyList<string>)x.Value.PageTypes, x.Value.Shape));

    public static bool IsPluginCondition(string name) => name?.Contains('.') == true;

    public static string Vocabulary(string pageType) =>
        string.Join("; ", BuiltIn.Where(x => x.Value.PageTypes.Contains(pageType)).Select(x => $"{x.Key}: {x.Value.Shape}"));

    public static JsonNode Normalize(string name, string pageType, JsonNode value, out string error)
    {
        error = null;
        if (!BuiltIn.TryGetValue(name, out var condition))
        {
            if (IsPluginCondition(name))
            {
                return value?.DeepClone();
            }

            error = $"'{name}' is not a condition of the storefront. Conditions of {pageType} rules: {Vocabulary(pageType)}.";
            return null;
        }

        if (!condition.PageTypes.Contains(pageType))
        {
            error = $"'{name}' does not apply to {pageType} rules. Conditions of {pageType} rules: {Vocabulary(pageType)}.";
            return null;
        }

        var result = NormalizeBuiltIn(name, value);

        if (result == null)
        {
            error = $"The value of '{name}' must be {condition.Shape}.";
        }

        return result;
    }

    private static JsonNode NormalizeBuiltIn(string name, JsonNode value) => name switch
    {
        "inCategory" => CategoryIds(value),
        "property" or "setting" => NamedValue(value),
        "source" => GetString(value) is PagesObjectType or ContentFileObjectType ? value.DeepClone() : null,
        _ => string.IsNullOrWhiteSpace(GetString(value)) ? null : JsonValue.Create(GetString(value).Trim()),
    };

    // Null where only the storefront can tell: a plugin condition, or catalog data of a product or category, which is not loaded.
    public static bool? Evaluate(string name, JsonNode value, string pageType, PageRuleSubject subject, out string notEvaluated)
    {
        notEvaluated = null;
        if (!BuiltIn.TryGetValue(name, out var condition))
        {
            notEvaluated = "a condition of a storefront plugin, which only the storefront evaluates";
            return null;
        }

        // the storefront never matches a rule on a condition that does not apply to its page type
        if (!condition.PageTypes.Contains(pageType))
        {
            return false;
        }

        if (subject == null)
        {
            notEvaluated = "it reads the catalog data of the product or category (categories, type, properties), which this calculation does not load";
            return null;
        }

        return EvaluateOnSubject(name, value, subject);
    }

    private static bool EvaluateOnSubject(string name, JsonNode value, PageRuleSubject subject)
    {
        switch (name)
        {
            case "path":
                return GetString(value) is { } glob && MatchesPath(subject.Permalink, glob);
            case "language":
                return GetString(value) is { } language && MatchesLanguage(subject.Language, language);
            case "source":
                return GetString(value) is { } source && subject.ObjectType == source;
            default:
                return MatchesSetting(value, subject);
        }
    }

    private static bool MatchesLanguage(string subjectLanguage, string language) =>
        !string.IsNullOrEmpty(subjectLanguage) && (subjectLanguage == language || subjectLanguage.Split('-')[0] == language);

    private static bool MatchesSetting(JsonNode value, PageRuleSubject subject) =>
        value is JsonObject named && GetString(named["name"]) is { } settingName &&
        JsonNode.DeepEquals(subject.Settings?[settingName], named["equals"]);

    public static bool MatchesPath(string permalink, string glob)
    {
        if (permalink == null)
        {
            return false;
        }

        var pattern = string.Join(".*", glob.Trim().Trim('/').Split('*').Select(Regex.Escape));

        return Regex.IsMatch(permalink.Trim().Trim('/'), $"^{pattern}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    [SuppressMessage("Major Code Smell", "S1168:Empty arrays and collections should be returned instead of null",
        Justification = "null tells Normalize the value is invalid; an empty one would be stored as a valid condition.")]
    private static JsonArray CategoryIds(JsonNode value)
    {
        var ids = value switch
        {
            JsonArray list => list.Select(GetString).ToList(),
            JsonValue => (GetString(value) ?? string.Empty).Split(',').Select(x => x.Trim()).ToList(),
            _ => [],
        };

        return ids.Count > 0 && ids.All(x => !string.IsNullOrWhiteSpace(x)) ? new JsonArray([.. ids.Select(x => (JsonNode)x.Trim())]) : null;
    }

    [SuppressMessage("Major Code Smell", "S1168:Empty arrays and collections should be returned instead of null",
        Justification = "null tells Normalize the value is invalid; an empty one would be stored as a valid condition.")]
    private static JsonObject NamedValue(JsonNode value)
    {
        if (value is not JsonObject named || string.IsNullOrWhiteSpace(GetString(named["name"])))
        {
            return null;
        }

        return GetString(named["equals"]) is { } equals && !string.IsNullOrWhiteSpace(equals)
            ? new JsonObject { ["name"] = GetString(named["name"]).Trim(), ["equals"] = equals }
            : null;
    }
}
