using System.Text.Json;
using System.Text.Json.Nodes;
using VirtoCommerce.SiteBuilder.Data.Theme;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

/// <summary>
/// The settings a new version of a custom section type adds and removes. A placed section keeps the settings it holds: a new setting shows its
/// default there, and a removed one is held but read by nothing.
/// </summary>
public sealed record SectionTypeSettingChanges(IList<NewSettingRow> Added, IList<string> Removed)
{
    private const int MaxDefaultLength = 80;
    private static readonly string[] LabelFieldTypes = ["header", "paragraph"];

    public static readonly SectionTypeSettingChanges None = new([], []);

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;

    public static SectionTypeSettingChanges Compare(JsonObject beforeSchema, JsonObject afterSchema)
    {
        var before = GetSettings(beforeSchema);
        var after = GetSettings(afterSchema);

        return new(
            after.Where(x => !before.ContainsKey(x.Key)).Select(x => new NewSettingRow(x.Key, DescribeDefault(x.Value))).ToList(),
            before.Keys.Where(x => !after.ContainsKey(x)).ToList());
    }

    /// <summary>The schema of a stored type, or <c>null</c> when it has none or is not JSON.</summary>
    public static JsonObject ReadSchema(string content)
    {
        try
        {
            return (JsonNode.Parse(content ?? "") as JsonObject)?[ThemeCustomSections.SchemaKey] as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public SectionTypePlacements ToPlacements(IList<PlacingPageRow> pages, string coverage) => new()
    {
        NewSettings = Added,
        RemovedSettings = Removed,
        Pages = pages,
        Coverage = coverage,
        Note = Removed.Count > 0
            ? "A removed setting stays in the placed sections, but the type no longer reads it: what pages hold in it is no longer shown."
            : null,
    };

    private static Dictionary<string, JsonNode> GetSettings(JsonObject schema) =>
        (schema?["settings"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(x => x["id"] is JsonValue && !LabelFieldTypes.Contains(GetString(x["type"]), StringComparer.Ordinal))
            .GroupBy(x => x["id"]!.ToString(), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First()["default"], StringComparer.Ordinal);

    private static string DescribeDefault(JsonNode value) => value switch
    {
        null => "nothing: the setting has no default",
        JsonArray rows => $"its default, {rows.Count} rows",
        JsonValue text when text.TryGetValue<string>(out var str) => string.IsNullOrEmpty(str)
            ? "nothing: its default is empty"
            : $"its default '{(str.Length > MaxDefaultLength ? str[..MaxDefaultLength] + "…" : str)}'",
        _ => $"its default {value.ToJsonString()}",
    };
}
