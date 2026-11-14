using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>How a theme's documents and their values are read against the schema catalog, the same for validating them and for building the snapshot.</summary>
public abstract class ThemeContentReader
{
    protected const string ContentPageType = "page";

    private const string SharedSectionFields = "_sections";
    private const string SharedBlockFields = "_blocks";

    /// <summary>Also accepts the legacy array format.</summary>
    [SuppressMessage("Major Code Smell", "S1168:Empty arrays and collections should be returned instead of null",
        Justification = "null means the token is not a page; an empty document would be read as a page without settings or sections.")]
    protected virtual JObject GetPageDocument(JToken token)
    {
        var (settings, content) = token switch
        {
            JObject document => (document["settings"], document["content"]),
            JArray legacy when legacy.Count > 0 => (legacy[0], new JArray(legacy.Skip(1))),
            _ => (null, null),
        };

        if (!IsPageDocument(token, settings, content))
        {
            return null;
        }

        return new JObject
        {
            ["settings"] = settings?.DeepClone() ?? new JObject { ["type"] = "settings" },
            ["content"] = content?.DeepClone() ?? new JArray(),
        };
    }

    protected virtual IList<JObject> GetFields(JObject schema, string kind, JObject catalog)
    {
        if (schema == null)
        {
            return [];
        }

        var ownFields = (schema["settings"] as JArray)?.OfType<JObject>().ToList() ?? [];
        var ownIds = ownFields.Select(x => GetString(x, "id")).Where(x => x != null).ToHashSet(StringComparer.Ordinal);
        var sharedFields = GetSharedFields(schema, kind, catalog);

        return ownFields.Concat(sharedFields.Where(x => !ownIds.Contains(GetString(x, "id") ?? string.Empty))).ToList();
    }

    protected static void VisitValues(JObject values, IList<JObject> fields, string path, Action<JObject, JToken, string> visit)
    {
        foreach (var field in fields)
        {
            if (GetString(field, "id") is not { } id)
            {
                continue;
            }

            var value = values[id];
            var fieldPath = path == null ? id : $"{path}.{id}";
            visit(field, value, fieldPath);

            if (field["element"] is not JArray element)
            {
                continue;
            }

            var elementFields = element.OfType<JObject>().ToList();
            if (value is JObject single)
            {
                VisitValues(single, elementFields, fieldPath, visit);
            }

            var rows = value as JArray ?? [];
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i] is JObject row)
                {
                    VisitValues(row, elementFields, $"{fieldPath}[{i}]", visit);
                }
            }
        }
    }

    protected static string GetString(JObject item, string name) => item[name] is JValue { Type: JTokenType.String } value ? (string)value : null;

    protected static IEnumerable<string> GetStrings(JToken list) =>
        (list as JArray)?.OfType<JValue>().Where(x => x.Type == JTokenType.String).Select(x => (string)x) ?? [];

    // a block's schema is looked up in blocks, then sections, as the designer does
    protected static JObject GetItemSchema(JObject catalog, string kind, string type) =>
        GetSchema(catalog, kind, type) ?? (kind == ModuleConstants.SchemaKinds.Blocks ? GetSchema(catalog, ModuleConstants.SchemaKinds.Sections, type) : null);

    [SuppressMessage("Major Code Smell", "S1168:Empty arrays and collections should be returned instead of null",
        Justification = "null means the catalog has no such schema; an empty schema would be read as one without fields.")]
    private static JObject GetSchema(JObject catalog, string kind, string type) =>
        type == null ? null : (catalog?[kind] as JObject)?[type] as JObject;

    private static bool IsPageDocument(JToken token, JToken settings, JToken content) =>
        token is JObject or JArray && settings is null or JObject && content is null or JArray;

    private static IEnumerable<JObject> GetSharedFields(JObject schema, string kind, JObject catalog)
    {
        var defaultSharedKey = kind == ModuleConstants.SchemaKinds.Sections ? SharedSectionFields : SharedBlockFields;
        var includedFields = GetStrings(schema["includeShared"]).SelectMany(key => GetSharedFields(catalog, key));

        return schema["excludeShared"] switch
        {
            JValue { Type: JTokenType.Boolean } exclude when exclude.Value<bool>() => includedFields,
            JArray excluded => GetSharedFields(catalog, defaultSharedKey).Concat(includedFields)
                .Where(x => !GetStrings(excluded).Contains(GetString(x, "id"))),
            _ => GetSharedFields(catalog, defaultSharedKey).Concat(includedFields),
        };
    }

    private static IEnumerable<JObject> GetSharedFields(JObject catalog, string key) =>
        (GetSchema(catalog, ModuleConstants.SchemaKinds.Shared, key)?["settings"] as JArray)?.OfType<JObject>() ?? [];
}
