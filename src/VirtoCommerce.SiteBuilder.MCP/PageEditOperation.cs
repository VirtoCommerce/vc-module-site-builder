using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VirtoCommerce.SiteBuilder.MCP;

// One change of edit_page_draft. Which fields an operation reads is listed in the description of "op".
public sealed class PageEditOperation
{
    [JsonPropertyName("op")]
    [Description("What to do. add_section (type, settings, place), duplicate_section (section_id), move_section " +
        "(section_id, place), hide_section / show_section (section_id), remove_section (section_id), " +
        "set_section_settings (section_id, settings, optional block_id and language), add_block (section_id, type, " +
        "settings, place among the blocks), duplicate_block / hide_block / show_block / remove_block (section_id, " +
        "block_id), move_block (section_id, block_id, place among the blocks), set_page_settings (settings, optional " +
        "language), copy_sections_from (source_page_id, optional section_ids, place), place_shared_component " +
        "(shared_component_id, place), detach_shared_component (section_id of the placement). A place is position, " +
        "after or before; without one a new section goes to the end of the page and a new block to the end of its section.")]
    public string Op { get; set; }

    [JsonPropertyName("section_id")]
    [Description("Id of a section of the page, as search_site_pages with page_id returns it.")]
    public string SectionId { get; set; }

    [JsonPropertyName("block_id")]
    [Description("Id of a block of that section.")]
    public string BlockId { get; set; }

    [JsonPropertyName("type")]
    [Description("Section or block type to add, for example 'hero-banner'. Use the types the store's pages already use.")]
    public string Type { get; set; }

    [JsonPropertyName("settings")]
    [Description("Values to set: a key is a setting name or a dotted path into nested values, for example 'title', " +
        "'button.url' or 'seoInfo.pageTitle'; in a list, a path step is the id of an item or its 0-based index, so " +
        "'blocks.b1.text' is the text of block b1. null removes the setting. Other settings stay as they are.")]
    public Dictionary<string, JsonNode> Settings { get; set; }

    [JsonPropertyName("language")]
    [Description("For a setting that holds a text per language (an object such as { \"en-US\": \"...\", \"fr-FR\": \"...\" }): " +
        "the language whose text the value sets; the other languages stay.")]
    public string Language { get; set; }

    [JsonPropertyName("position")]
    [Description("0-based place among the page's sections (or the section's blocks) that the item takes; 0 is first.")]
    public int? Position { get; set; }

    [JsonPropertyName("after")]
    [Description("Id of the section (or block) the item goes right after.")]
    public string After { get; set; }

    [JsonPropertyName("before")]
    [Description("Id of the section (or block) the item goes right before.")]
    public string Before { get; set; }

    [JsonPropertyName("source_page_id")]
    [Description("copy_sections_from: the page to copy sections from; you need builder:read in its store.")]
    public string SourcePageId { get; set; }

    [JsonPropertyName("section_ids")]
    [Description("copy_sections_from: ids of the sections to copy, in the source page's order. Omit it to copy every section.")]
    public IList<string> SectionIds { get; set; }

    [JsonPropertyName("shared_component_id")]
    [Description("place_shared_component: the Shared Component of the page's store to place (search_shared_components).")]
    public string SharedComponentId { get; set; }
}
