using System.Text.Json;
using System.Text.Json.Nodes;

namespace VirtoCommerce.SiteBuilder.Data.Services;

// The settings and the sections of a stored page, template or Shared Component document. Pages are stored as
// { settings, content: [...] }, older ones as [settings, ...sections]; both are read into the first shape, with an id
// on every section and block (see SiteBuilderPageItems.AssignMissingIds), so that whoever reads a version and whoever
// edits the same version name its sections alike.
public sealed class SiteBuilderPageDocument
{
    public JsonObject Settings { get; init; }

    public JsonArray Sections { get; init; }

    // Stored as [settings, ...sections]: the store indexes no Shared Component placed in such a page.
    public bool IsLegacyShape { get; init; }

    // Null when there is no document, or it is not JSON of either shape.
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

        SiteBuilderPageDocument result = null;
        if (root is JsonObject document && document["content"] is JsonArray sections)
        {
            result = new SiteBuilderPageDocument
            {
                Settings = (document["settings"] as JsonObject)?.DeepClone() as JsonObject ?? [],
                Sections = (JsonArray)sections.DeepClone(),
            };
        }
        else if (root is JsonArray legacy && legacy.Count > 0 && legacy[0] is JsonObject legacySettings)
        {
            result = new SiteBuilderPageDocument
            {
                Settings = (JsonObject)legacySettings.DeepClone(),
                Sections = [.. legacy.Skip(1).Select(x => x?.DeepClone())],
                IsLegacyShape = true,
            };
        }

        if (result != null)
        {
            SiteBuilderPageItems.AssignMissingIds(result.Sections);
        }

        return result;
    }
}
