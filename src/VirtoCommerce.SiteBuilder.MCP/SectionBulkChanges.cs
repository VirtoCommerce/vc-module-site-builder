using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace VirtoCommerce.SiteBuilder.MCP;

// The one change update_sections_bulk makes to every matching section, as the operations of edit_page_draft.
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
            throw new McpException($"{op} takes no settings and no settings_language.");
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

    public static Dictionary<string, JsonNode> ReadSettings(string json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Dictionary<string, JsonNode>>(json);
}
