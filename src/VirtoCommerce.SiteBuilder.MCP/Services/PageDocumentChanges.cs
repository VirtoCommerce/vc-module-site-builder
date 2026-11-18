using System.Text.Json.Nodes;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

public sealed record PageDocumentChange(string Field, string From, string To);

// What an edit changes in a page, section by section, for the agent and the approver to read: sections added,
// removed, moved and hidden, and each setting that changed with its old and new value.
public static class PageDocumentChanges
{
    private const int MaxValueLength = 300;
    private static readonly string[] ItemKeys = [IdKey, TypeKey, HiddenKey, BlocksKey];

    public static IList<PageDocumentChange> Compare(PageDocumentEditor before, PageDocumentEditor after)
    {
        var changes = new List<PageDocumentChange>();

        CompareValues(changes, "page settings", Flatten(before.Settings, []), Flatten(after.Settings, []));
        CompareItems(changes, "section", "order of sections", Items(before.Sections), Items(after.Sections));

        return changes;
    }

    private static void CompareItems(List<PageDocumentChange> changes, string kind, string orderField, IList<JsonObject> before, IList<JsonObject> after)
    {
        var beforeIds = before.Select(GetId).ToList();
        var afterIds = after.Select(GetId).ToList();

        for (var i = 0; i < before.Count; i++)
        {
            if (!afterIds.Contains(beforeIds[i]))
            {
                changes.Add(new($"{kind} {Describe(before[i])}", $"at position {i}", "removed"));
            }
        }

        var kept = beforeIds.Where(afterIds.Contains).ToList();
        if (!kept.SequenceEqual(afterIds.Where(beforeIds.Contains)))
        {
            changes.Add(new(orderField, string.Join(", ", before.Select(Describe)), string.Join(", ", after.Select(Describe))));
        }

        for (var i = 0; i < after.Count; i++)
        {
            var item = after[i];
            var index = beforeIds.IndexOf(afterIds[i]);
            var label = $"{kind} {Describe(item)}";
            if (index < 0)
            {
                changes.Add(new(label, null, $"added at position {i}"));
                CompareItem(changes, label, null, item);
            }
            else
            {
                CompareItem(changes, label, before[index], item);
            }
        }
    }

    private static void CompareItem(List<PageDocumentChange> changes, string label, JsonObject before, JsonObject after)
    {
        if (IsPlacement(after))
        {
            return;
        }

        var wasHidden = before != null && IsHidden(before);
        if (wasHidden != IsHidden(after))
        {
            changes.Add(new($"{label} hidden", wasHidden ? "true" : "false", IsHidden(after) ? "true" : "false"));
        }

        CompareValues(changes, label, Flatten(before, ItemKeys), Flatten(after, ItemKeys));
        CompareItems(changes, $"{label} block", $"{label} order of blocks", Items(before == null ? null : GetBlocks(before)), Items(GetBlocks(after)));
    }

    private static void CompareValues(List<PageDocumentChange> changes, string label, Dictionary<string, JsonNode> before, Dictionary<string, JsonNode> after)
    {
        foreach (var path in before.Keys.Concat(after.Keys.Where(x => !before.ContainsKey(x))))
        {
            var hasFrom = before.TryGetValue(path, out var from);
            var hasTo = after.TryGetValue(path, out var to);
            if (hasFrom != hasTo || !JsonNode.DeepEquals(from, to))
            {
                changes.Add(new($"{label}: {path}", hasFrom ? Render(from) : null, hasTo ? Render(to) : null));
            }
        }
    }

    private static List<JsonObject> Items(JsonArray items) =>
        items?.OfType<JsonObject>().Where(x => !string.IsNullOrEmpty(GetId(x))).ToList() ?? [];

    // Every value by its dotted path; a list is one value, as the designer's settings panel edits it.
    private static Dictionary<string, JsonNode> Flatten(JsonObject item, string[] skippedKeys)
    {
        var values = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        if (item != null)
        {
            foreach (var (key, value) in item.Where(x => !skippedKeys.Contains(x.Key)))
            {
                Flatten(values, key, value);
            }
        }

        return values;
    }

    private static void Flatten(Dictionary<string, JsonNode> values, string path, JsonNode node)
    {
        if (node is JsonObject value && value.Count > 0)
        {
            foreach (var (key, child) in value)
            {
                Flatten(values, $"{path}.{key}", child);
            }

            return;
        }

        values[path] = node;
    }

    private static string Render(JsonNode node)
    {
        var text = node == null ? "null" : GetString(node) ?? node.ToJsonString(PageDocumentEditor.WriteOptions);
        return text.Length > MaxValueLength ? $"{text[..MaxValueLength]}…" : text;
    }
}
