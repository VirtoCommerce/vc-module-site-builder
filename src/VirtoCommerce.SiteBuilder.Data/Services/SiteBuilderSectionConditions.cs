using System.Text.Json.Nodes;
using VirtoCommerce.SiteBuilder.Core.Models;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.Data.Services;

// Matches a section of a document against a type and a condition on its settings, as SiteBuilderSectionSettingCondition
// describes it. The settings of a section are its own keys, as the designer stores them.
public static class SiteBuilderSectionConditions
{
    public const string AnyItem = "*";

    public static bool IsMatch(JsonObject section, string sectionType, SiteBuilderSectionSettingCondition condition)
    {
        if (section == null || !string.Equals(GetItemType(section), sectionType, StringComparison.Ordinal))
        {
            return false;
        }

        return condition == null || Resolve(section, condition.Path.Split('.')).Any(x => IsMatch(x, condition));
    }

    // Null when the condition can be checked; otherwise why not, for the caller to say.
    public static string Validate(SiteBuilderSectionSettingCondition condition)
    {
        if (condition == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(condition.Path) || condition.Path.Split('.').Any(string.IsNullOrWhiteSpace))
        {
            return $"'{condition.Path}' is not a setting path such as 'title' or 'button.url'.";
        }

        if (condition.Operator is not (SiteBuilderSectionSettingCondition.EqualsOperator or SiteBuilderSectionSettingCondition.ContainsOperator))
        {
            return $"'{condition.Operator}' is not an operator: use {SiteBuilderSectionSettingCondition.EqualsOperator} or {SiteBuilderSectionSettingCondition.ContainsOperator}.";
        }

        if (condition.Value == null)
        {
            return "The condition needs a value to compare with.";
        }

        if (condition.Language != null && !IsLanguageCode(condition.Language))
        {
            return $"'{condition.Language}' is not a language code such as en-US.";
        }

        return null;
    }

    private static IEnumerable<JsonNode> Resolve(JsonNode node, string[] segments)
    {
        if (segments.Length == 0)
        {
            return [node];
        }

        var (head, rest) = (segments[0], segments[1..]);

        return node switch
        {
            JsonObject value when value.TryGetPropertyValue(head, out var child) && child != null => Resolve(child, rest),
            JsonArray list when head == AnyItem => list.Where(x => x != null).SelectMany(x => Resolve(x, rest)),
            JsonArray list => FindItem(list, head) is { } item ? Resolve(item, rest) : [],
            _ => [],
        };
    }

    // An item of a list by its id, else by its 0-based index, as the settings paths of edit_page_draft name them.
    private static JsonNode FindItem(JsonArray list, string segment)
    {
        var byId = list.OfType<JsonObject>().FirstOrDefault(x => GetId(x) == segment);
        if (byId != null)
        {
            return byId;
        }

        return int.TryParse(segment, out var index) && index >= 0 && index < list.Count ? list[index] : null;
    }

    private static bool IsMatch(JsonNode value, SiteBuilderSectionSettingCondition condition)
    {
        if (IsTranslations(value))
        {
            var texts = ((JsonObject)value)
                .Where(x => condition.Language == null || string.Equals(x.Key, condition.Language, StringComparison.OrdinalIgnoreCase))
                .Select(x => GetString(x.Value));

            return texts.Any(x => IsMatch(x, condition));
        }

        return value is JsonValue scalar && IsMatch(GetString(scalar) ?? scalar.ToJsonString(), condition);
    }

    private static bool IsMatch(string text, SiteBuilderSectionSettingCondition condition)
    {
        if (text == null)
        {
            return false;
        }

        return condition.Operator == SiteBuilderSectionSettingCondition.ContainsOperator
            ? text.Contains(condition.Value, StringComparison.OrdinalIgnoreCase)
            : string.Equals(text, condition.Value, StringComparison.Ordinal);
    }
}
