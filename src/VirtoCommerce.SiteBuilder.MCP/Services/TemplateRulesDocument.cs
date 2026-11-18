using System.Text.Json;
using System.Text.Json.Nodes;
using VirtoCommerce.SiteBuilder.Data.Theme;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

// Reads and writes the rules file as the designer does; what the file holds that the designer does not know is kept as it is.
public sealed class TemplateRulesDocument
{
    public const int Version = 2;

    public static readonly string[] PageTypes = ["product", "category", "page"];

    private readonly JsonObject _rest;
    private readonly Dictionary<string, List<JsonObject>> _lists;

    private TemplateRulesDocument(JsonObject rest, Dictionary<string, List<JsonObject>> lists, int unreadable)
    {
        _rest = rest;
        _lists = lists;
        Unreadable = unreadable;
    }

    // Rules neither the designer nor the storefront can read are dropped, as the designer drops them when it saves.
    public int Unreadable { get; }

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

    // Without its own list a page type gets the storefront's default rules.
    public bool HasOwnRules(string pageType) => _lists.ContainsKey(pageType);

    // Without its own list, a copy of the defaults, which becomes the type's own once set.
    public List<JsonObject> RulesOf(string pageType)
    {
        if (_lists.TryGetValue(pageType, out var own))
        {
            return own;
        }

        return WithIds(JsonNode.Parse(ThemeSnapshotBuilder.DefaultTemplateRulesJson)[pageType] as JsonArray ?? [], pageType);
    }

    public void SetRules(string pageType, List<JsonObject> rules) => _lists[pageType] = rules;

    public IEnumerable<string> AllIds() => PageTypes.SelectMany(type => RulesOf(type).Select(GetId));

    // In the designer's key order.
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

    private static List<JsonObject> WithIds(JsonArray list, string pageType)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<JsonObject>();
        foreach (var node in list.Where(IsRuleLike))
        {
            var rule = (JsonObject)node.DeepClone();
            var id = ThemeSnapshotBuilder.GetTemplateRuleId(pageType, result.Count, GetString(rule["id"]), ids);
            ids.Add(id);
            rule["id"] = id;
            result.Add(rule);
        }

        return result;
    }
}
