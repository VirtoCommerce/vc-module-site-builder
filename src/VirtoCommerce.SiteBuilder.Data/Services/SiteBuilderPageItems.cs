using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;

namespace VirtoCommerce.SiteBuilder.Data.Services;

// Follows the designer's rules for sections and blocks, so that items read or written here look the same to it.
// A Shared Component placement holds its id, type and reference and nothing else.
public static partial class SiteBuilderPageItems
{
    public const string IdKey = "id";
    public const string TypeKey = "type";
    public const string HiddenKey = "hidden";
    public const string BlocksKey = "blocks";
    public const string GroupKey = "__group";

    private const string IdCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int IdSuffixLength = 8;

    public static string GetString(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static string GetId(JsonNode item) => GetString((item as JsonObject)?[IdKey]);

    public static string GetItemType(JsonNode item) => GetString((item as JsonObject)?[TypeKey]);

    public static bool IsHidden(JsonObject item) => item[HiddenKey] is JsonValue value && value.TryGetValue<bool>(out var hidden) && hidden;

    public static bool IsInGroup(JsonObject item) => item.ContainsKey(GroupKey);

    public static bool IsPlacement(JsonObject item) =>
        item.Count == 3 && GetItemType(item) == SharedComponents.ReferenceType &&
        !string.IsNullOrWhiteSpace(GetId(item)) && !string.IsNullOrWhiteSpace(GetString(item[SharedComponents.ReferenceProperty]));

    public static string GetComponentId(JsonObject item) => GetString(item[SharedComponents.ReferenceProperty]);

    public static JsonArray GetBlocks(JsonObject item) => item[BlocksKey] as JsonArray;

    // For the messages the agent and the approver read.
    public static string Describe(JsonObject item) => IsPlacement(item)
        ? $"Shared Component '{GetComponentId(item)}' (placement '{GetId(item)}')"
        : $"{GetItemType(item) ?? "untyped"} '{GetId(item)}'";

    // Stored sections and blocks may have no id: the designer gives them one when it opens the page. Here they get
    // one from their place, so that reading a version and editing the same version name them alike.
    public static void AssignMissingIds(JsonArray sections)
    {
        var used = CollectIds(sections);
        for (var i = 0; i < sections.Count; i++)
        {
            if (sections[i] is not JsonObject section)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(GetId(section)))
            {
                section[IdKey] = Unique($"{Slug(GetItemType(section))}{i + 1}", used);
            }

            var blocks = GetBlocks(section);
            for (var j = 0; blocks != null && j < blocks.Count; j++)
            {
                if (blocks[j] is JsonObject block && string.IsNullOrWhiteSpace(GetId(block)))
                {
                    block[IdKey] = Unique($"{GetId(section)}-{Slug(GetItemType(block))}{j + 1}", used);
                }
            }
        }
    }

    // The designer's id format.
    public static string NewId(string type, ISet<string> used) =>
        Unique(Slug(type) + RandomNumberGenerator.GetString(IdCharacters, IdSuffixLength), used);

    public static string NewPlacementId(ISet<string> used) => NewId(SharedComponents.ReferenceType, used);

    // Copies as the designer does, so a copy made here looks like one made there.
    public static JsonObject Copy(JsonObject item, ISet<string> used)
    {
        var copy = (JsonObject)item.DeepClone();
        copy.Remove(GroupKey);
        if (IsPlacement(item))
        {
            copy[IdKey] = NewPlacementId(used);
            return copy;
        }

        copy[IdKey] = NewId(GetItemType(item), used);
        if (GetBlocks(copy) is { } blocks)
        {
            foreach (var block in blocks.OfType<JsonObject>())
            {
                block[IdKey] = NewId(GetItemType(block), used);
            }
        }

        return copy;
    }

    public static HashSet<string> CollectIds(JsonArray sections)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections.OfType<JsonObject>())
        {
            AddId(ids, section);
            foreach (var block in GetBlocks(section)?.OfType<JsonObject>() ?? [])
            {
                AddId(ids, block);
            }
        }

        return ids;
    }

    private static void AddId(HashSet<string> ids, JsonObject item)
    {
        var id = GetId(item);
        if (!string.IsNullOrWhiteSpace(id))
        {
            ids.Add(id);
        }
    }

    public static bool IsLanguageCode(string value) => value != null && LanguagePattern().IsMatch(value);

    // The designer's rule. A key with a subtag is required, or a row such as { "sku": "X1" } would pass for texts per language.
    public static bool IsTranslations(JsonNode node)
    {
        return node is JsonObject value && value.Count > 0
            && value.Any(x => LanguageWithSubtagPattern().IsMatch(x.Key))
            && value.All(x => LanguagePattern().IsMatch(x.Key) && x.Value is JsonValue text && text.TryGetValue<string>(out _));
    }

    private static string Slug(string type)
    {
        var slug = new string((type ?? string.Empty).Where(char.IsAsciiLetterOrDigit).ToArray());
        return slug.Length == 0 ? "item" : slug;
    }

    private static string Unique(string id, ISet<string> used)
    {
        var candidate = id;
        for (var n = 2; !used.Add(candidate); n++)
        {
            candidate = $"{id}-{n}";
        }

        return candidate;
    }

    [GeneratedRegex("^[a-z]{2,3}(-[a-z0-9]{2,8})*$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguagePattern();

    [GeneratedRegex("^[a-z]{2,3}(-[a-z0-9]{2,8})+$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageWithSubtagPattern();
}
