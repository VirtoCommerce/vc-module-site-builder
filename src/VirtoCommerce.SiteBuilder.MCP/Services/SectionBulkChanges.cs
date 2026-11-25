using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

public static class SectionBulkChanges
{
    public const string SetSectionSettings = "set_section_settings";
    public const string HideSection = "hide_section";
    public const string ShowSection = "show_section";
    public const string RemoveSection = "remove_section";

    private static readonly string[] Known = [SetSectionSettings, HideSection, ShowSection, RemoveSection];

    public static string Parse(string change, Dictionary<string, JsonNode> settings, string language)
    {
        var op = Known.FirstOrDefault(x => string.Equals(x, change?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"change must be one of {string.Join(", ", Known)}.");

        if (op == SetSectionSettings && (settings == null || settings.Count == 0))
        {
            throw new McpException("set_section_settings needs settings: the values to set on every matching section.");
        }

        if (op != SetSectionSettings && (settings?.Count > 0 || language != null))
        {
            throw new McpException($"{op} takes no settings and no text_language.");
        }

        return op;
    }

    public static IReadOnlyList<PageEditOperation> CreateOperations(string change, Dictionary<string, JsonNode> settings, string language,
        IEnumerable<string> sectionIds)
    {
        return sectionIds
            .Select(id => new PageEditOperation
            {
                Op = change,
                SectionId = id,
                Settings = settings?.ToDictionary(x => x.Key, x => x.Value?.DeepClone()),
                Language = language,
            })
            .ToList();
    }

    // The job payload is stored as JSON by another serializer, so the settings travel as their own JSON text.
    public static string WriteSettings(Dictionary<string, JsonNode> settings) =>
        settings == null ? null : JsonSerializer.Serialize(settings, PageDocumentEditor.WriteOptions);

    [SuppressMessage("Major Code Smell", "S1168:Empty arrays and collections should be returned instead of null",
        Justification = "null means the change passes no settings; an empty dictionary is refused as settings with no values.")]
    public static Dictionary<string, JsonNode> ReadSettings(string json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Dictionary<string, JsonNode>>(json);
}
