using System.Text.Json;
using System.Text.Json.Nodes;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.MCP;

// The theme's template rules file (config/template_rules.json) as the designer reads and writes it (rules.ts): a list
// of rules per page type, each { id, template, when?, disabled? }; the first enabled rule whose conditions all hold
// picks the template. A page type without its own list uses the storefront's default rules. What the file holds that
// the designer does not know is kept as it is.
public sealed class TemplateRulesDocument
{
    public const int Version = 2;

    public static readonly string[] PageTypes = ["product", "category", "page"];

    // As the storefront, the designer and the snapshot builder default them.
    private static readonly Dictionary<string, string> DefaultRules = new(StringComparer.Ordinal)
    {
        ["product"] = """[{ "id": "default-b2c", "template": "product.b2c", "when": { "property": { "name": "VirtoFrontend_UI_Layout", "equals": "B2C" } } }]""",
        ["category"] = "[]",
        ["page"] = "[]",
    };

    private readonly JsonObject _rest;
    private readonly Dictionary<string, List<JsonObject>> _lists;

    private TemplateRulesDocument(JsonObject rest, Dictionary<string, List<JsonObject>> lists, int unreadable)
    {
        _rest = rest;
        _lists = lists;
        Unreadable = unreadable;
    }

    // Rules the designer and the storefront cannot read (no template, or conditions that are not an object): they are
    // left out, as the designer leaves them out when it saves.
    public int Unreadable { get; }

    // Null when the file holds something other than an object; an empty or missing file has no rules of its own.
    public static TemplateRulesDocument Read(string content)
    {
        JsonObject file;
        if (string.IsNullOrWhiteSpace(content))
        {
            file = [];
        }
        else
        {
            try
            {
                file = JsonNode.Parse(content) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (file == null)
        {
            return null;
        }

        var rest = (JsonObject)file.DeepClone();
        var lists = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        var unreadable = 0;
        foreach (var type in PageTypes)
        {
            if (rest[type] is JsonArray list)
            {
                unreadable += list.Count(x => !IsRuleLike(x));
                lists[type] = WithIds(list, type);
            }
            else if (rest[type] != null)
            {
                unreadable++;
            }

            rest.Remove(type);
        }

        rest.Remove("version");
        rest.Remove("conditions");

        return new TemplateRulesDocument(rest, lists, unreadable);
    }

    // The type has a list in the file; without one the storefront uses its default rules.
    public bool HasOwnRules(string pageType) => _lists.ContainsKey(pageType);

    // The rules of the type in order: its own, or a copy of the defaults, which become its own once changed.
    public List<JsonObject> RulesOf(string pageType)
    {
        if (_lists.TryGetValue(pageType, out var own))
        {
            return own;
        }

        return WithIds((JsonArray)JsonNode.Parse(DefaultRules[pageType]), pageType);
    }

    public void SetRules(string pageType, List<JsonObject> rules) => _lists[pageType] = rules;

    public IEnumerable<string> AllIds() => PageTypes.SelectMany(type => RulesOf(type).Select(GetId));

    // In the designer's order: version, conditions (every condition name the rules use), the lists, then the rest.
    public string Write()
    {
        var conditions = _lists.Values
            .SelectMany(x => x)
            .SelectMany(rule => (rule["when"] as JsonObject)?.Select(x => x.Key) ?? [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        var file = new JsonObject
        {
            ["version"] = Version,
            ["conditions"] = new JsonArray([.. conditions.Select(x => (JsonNode)x)]),
        };
        foreach (var type in PageTypes.Where(_lists.ContainsKey))
        {
            file[type] = new JsonArray([.. _lists[type].Select(x => x.DeepClone())]);
        }

        foreach (var (key, value) in _rest)
        {
            file[key] = value?.DeepClone();
        }

        return file.ToJsonString(new JsonSerializerOptions(PageDocumentEditor.WriteOptions) { WriteIndented = true });
    }

    public static string GetTemplate(JsonObject rule) => GetString(rule["template"])?.Trim();

    public static bool IsDisabled(JsonObject rule) => rule["disabled"] is JsonValue value && value.TryGetValue<bool>(out var disabled) && disabled;

    public static string PageTypeOf(string templateKey) => templateKey?.Split('.')[0] is { } type && PageTypes.Contains(type) ? type : null;

    private static bool IsRuleLike(JsonNode node) =>
        node is JsonObject rule && rule["template"] is JsonValue template && template.TryGetValue<string>(out _) && rule["when"] is null or JsonObject;

    // The ids the storefront, the designer and the snapshot builder give rules without a unique one.
    private static List<JsonObject> WithIds(JsonArray list, string pageType)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<JsonObject>();
        foreach (var node in list.Where(IsRuleLike))
        {
            // the place among the readable rules, as the others count it
            var index = result.Count;
            var rule = (JsonObject)node.DeepClone();
            var id = GetString(rule["id"]);
            if (string.IsNullOrEmpty(id) || ids.Contains(id))
            {
                id = $"{pageType}-{index}";
                for (var n = 2; ids.Contains(id); n++)
                {
                    id = $"{pageType}-{index}-{n}";
                }
            }

            ids.Add(id);
            rule["id"] = id;
            result.Add(rule);
        }

        return result;
    }
}
