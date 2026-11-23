using System.Text.Json;
using System.Text.Json.Nodes;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public sealed class SiteBuilderPageDocument
{
    public JsonObject Settings { get; init; }

    public JsonArray Sections { get; init; }

    public bool IsLegacyShape { get; init; }

    public static SiteBuilderPageDocument Read(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        JsonNode root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return null;
        }

        var result = FromPage(root) ?? FromLegacy(root);
        if (result != null)
        {
            SiteBuilderPageItems.AssignMissingIds(result.Sections);
        }

        return result;
    }

    private static SiteBuilderPageDocument FromPage(JsonNode root) =>
        root is JsonObject document && document["content"] is JsonArray sections
            ? new SiteBuilderPageDocument
            {
                Settings = (document["settings"] as JsonObject)?.DeepClone() as JsonObject ?? [],
                Sections = (JsonArray)sections.DeepClone(),
            }
            : null;

    private static SiteBuilderPageDocument FromLegacy(JsonNode root) =>
        root is JsonArray legacy && legacy.Count > 0 && legacy[0] is JsonObject legacySettings
            ? new SiteBuilderPageDocument
            {
                Settings = (JsonObject)legacySettings.DeepClone(),
                Sections = [.. legacy.Skip(1).Select(x => x?.DeepClone())],
                IsLegacyShape = true,
            }
            : null;
}
