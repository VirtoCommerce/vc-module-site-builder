using System.Text.Json;
using System.Text.Json.Nodes;

namespace VirtoCommerce.SiteBuilder.MCP;

// The settings and the sections of a stored page or Shared Component document. Pages are stored as
// { settings, content: [...] }, older ones as [settings, ...sections]; the agent gets both in the first shape, with an
// id on every section and block (see PageItems.AssignMissingIds), so that edit_page_draft can name them.
public sealed class PageContent
{
    public JsonObject Settings { get; init; }

    public JsonArray Sections { get; init; }

    // Stored as [settings, ...sections]: the store indexes no Shared Component placed in such a page.
    public bool IsLegacyShape { get; init; }

    // Null when there is no document, or it is not JSON of either shape.
    public static PageContent Read(string content)
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

        PageContent result = null;
        if (root is JsonObject document && document["content"] is JsonArray sections)
        {
            result = new PageContent
            {
                Settings = (document["settings"] as JsonObject)?.DeepClone() as JsonObject ?? [],
                Sections = (JsonArray)sections.DeepClone(),
            };
        }
        else if (root is JsonArray legacy && legacy.Count > 0 && legacy[0] is JsonObject legacySettings)
        {
            result = new PageContent
            {
                Settings = (JsonObject)legacySettings.DeepClone(),
                Sections = [.. legacy.Skip(1).Select(x => x?.DeepClone())],
                IsLegacyShape = true,
            };
        }

        if (result != null)
        {
            PageItems.AssignMissingIds(result.Sections);
        }

        return result;
    }
}
