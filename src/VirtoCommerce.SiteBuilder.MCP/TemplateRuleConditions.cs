using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.MCP;

// What a page of the database is to the template rules: as the storefront describes a page it shows.
public sealed record PageRuleSubject(string Permalink, string Language, JsonObject Settings, string ObjectType);

// The conditions of template rules the storefront has built in (its templates/conditions.ts): which page types each
// applies to and what its value looks like. Only the storefront evaluates rules; the server has no twin of these
// conditions, so the tools check a value's shape the way the designer's rules panel does, and the server-side
// calculation of get_preview evaluates only those whose data the server holds: a page's path, language, source and
// settings. A storefront plugin may add conditions of its own, named with a prefix ("loyalty.tier"); they are kept
// but not checked.
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

    public static bool IsBuiltIn(string name) => name != null && BuiltIn.ContainsKey(name);

    // Every built-in condition with the page types it applies to and the shape of its value, as the rules panel offers them.
    public static IEnumerable<(string Name, IReadOnlyList<string> PageTypes, string Shape)> All =>
        BuiltIn.Select(x => (x.Key, (IReadOnlyList<string>)x.Value.PageTypes, x.Value.Shape));

    public static bool IsPluginCondition(string name) => name?.Contains('.') == true;

    public static string Vocabulary(string pageType) =>
        string.Join("; ", BuiltIn.Where(x => x.Value.PageTypes.Contains(pageType)).Select(x => $"{x.Key}: {x.Value.Shape}"));

    // The value as the rules file keeps it, or the refusal. A list of category ids may come as a comma-separated text.
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

        JsonNode result = name switch
        {
            "inCategory" => CategoryIds(value),
            "property" or "setting" => NamedValue(value),
            "source" => GetString(value) is PagesObjectType or ContentFileObjectType ? value.DeepClone() : null,
            _ => string.IsNullOrWhiteSpace(GetString(value)) ? null : JsonValue.Create(GetString(value).Trim()),
        };

        if (result == null)
        {
            error = $"The value of '{name}' must be {condition.Shape}.";
        }

        return result;
    }

    // true or false where the server holds what the condition reads; null where only the storefront can tell. The
    // subject is a page of the database; a product or category is not loaded.
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

        switch (name)
        {
            case "path":
                return GetString(value) is { } glob && MatchesPath(subject.Permalink, glob);
            case "language":
                return GetString(value) is { } language && !string.IsNullOrEmpty(subject.Language) &&
                    (subject.Language == language || subject.Language.Split('-')[0] == language);
            case "source":
                return GetString(value) is { } source && subject.ObjectType == source;
            default:
                return value is JsonObject named && GetString(named["name"]) is { } settingName &&
                    JsonNode.DeepEquals(subject.Settings?[settingName], named["equals"]);
        }
    }

    // As the storefront's matchesPath: slashes around both are ignored, * is any characters, case is ignored.
    public static bool MatchesPath(string permalink, string glob)
    {
        if (permalink == null)
        {
            return false;
        }

        var pattern = string.Join(".*", glob.Trim().Trim('/').Split('*').Select(Regex.Escape));

        return Regex.IsMatch(permalink.Trim().Trim('/'), $"^{pattern}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

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

    private static JsonObject NamedValue(JsonNode value) =>
        value is JsonObject named && !string.IsNullOrWhiteSpace(GetString(named["name"])) && GetString(named["equals"]) is { } equals && !string.IsNullOrWhiteSpace(equals)
            ? new JsonObject { ["name"] = GetString(named["name"]).Trim(), ["equals"] = equals }
            : null;
}
