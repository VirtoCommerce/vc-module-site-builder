using System.Text.Json.Nodes;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

// A new section or block as the designer makes it (designer/schema.ts newItem): the defaults of its settings, then its schema's default, whose
// blocks are made the same way, each with the values the default gives it over its own defaults. A type without a schema starts empty.
public static class SchemaDefaults
{
    private const string SettingsKey = "settings";
    private const string DefaultKey = "default";

    public static JsonObject NewItem(JsonObject catalog, string type, bool block, ISet<string> used) => NewItem(catalog, type, block, used, values: null);

    private static JsonObject NewItem(JsonObject catalog, string type, bool block, ISet<string> used, JsonObject values)
    {
        var schema = (block ? GetSchema(catalog, SchemaKinds.Blocks, type) : null) ?? GetSchema(catalog, SchemaKinds.Sections, type);
        var item = new JsonObject { [IdKey] = NewId(type, used), [TypeKey] = type };
        Merge(item, GetValues(GetFields(catalog, schema, block)));
        Merge(item, schema?[DefaultKey] as JsonObject);
        Merge(item, values);

        if (schema?[BlocksKey] is JsonArray)
        {
            var blocks = GetBlocks(item)?.OfType<JsonObject>().Select(x => NewItem(catalog, GetItemType(x), block: true, used, x)) ?? [];
            item[BlocksKey] = new JsonArray([.. blocks]);
        }

        return item;
    }

    private static void Merge(JsonObject item, JsonObject values)
    {
        if (values == null)
        {
            return;
        }

        foreach (var (key, value) in values)
        {
            if (key is not (IdKey or TypeKey))
            {
                item[key] = value?.DeepClone();
            }
        }
    }

    // The schema's own settings, then the shared ones it does not define itself, in the order of their sort.
    private static IEnumerable<JsonObject> GetFields(JsonObject catalog, JsonObject schema, bool block)
    {
        if (schema == null)
        {
            return [];
        }

        var own = GetSettings(schema).ToList();
        var shared = catalog[SchemaKinds.Shared] as JsonObject;
        var included = GetStrings(schema["includeShared"]).SelectMany(x => GetSettings(shared?[x]));
        var exclude = schema["excludeShared"];
        var fromShared = exclude is JsonValue all && all.TryGetValue<bool>(out var excludesAll) && excludesAll
            ? included
            : GetSettings(shared?[block ? "_blocks" : "_sections"]).Concat(included)
                .Where(x => !(exclude is JsonArray excluded && GetId(x) is { Length: > 0 } id && GetStrings(excluded).Contains(id)));

        return own
            .Concat(fromShared.Where(x => !(GetId(x) is { Length: > 0 } id && own.Any(field => GetId(field) == id))))
            .OrderBy(x => x["sort"] is JsonValue sort && sort.TryGetValue<double>(out var value) ? value : 0);
    }

    private static JsonObject GetValues(IEnumerable<JsonObject> fields)
    {
        var values = new JsonObject();
        foreach (var field in fields)
        {
            if (GetId(field) is { Length: > 0 } id && GetDefault(field) is { HasValue: true } value)
            {
                values[id] = value.Node;
            }
        }

        return values;
    }

    private static (bool HasValue, JsonNode Node) GetDefault(JsonObject field)
    {
        if (field.TryGetPropertyValue(DefaultKey, out var value))
        {
            return (true, value?.DeepClone());
        }

        return GetString(field[TypeKey]) switch
        {
            "list" => (true, new JsonArray()),
            "object" => (true, GetValues(AsFields(field["element"]))),
            _ => (false, null),
        };
    }

    private static JsonObject GetSchema(JsonObject catalog, string kind, string type) =>
        type == null ? null : (catalog?[kind] as JsonObject)?[type] as JsonObject;

    private static IEnumerable<JsonObject> GetSettings(JsonNode schema) => AsFields((schema as JsonObject)?[SettingsKey]);

    private static IEnumerable<JsonObject> AsFields(JsonNode fields) => (fields as JsonArray)?.OfType<JsonObject>() ?? [];

    private static IEnumerable<string> GetStrings(JsonNode node) => (node as JsonArray)?.Select(GetString).Where(x => x != null) ?? [];
}
