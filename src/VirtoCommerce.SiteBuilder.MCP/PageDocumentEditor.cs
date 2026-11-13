using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using VirtoCommerce.SiteBuilder.Data.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.MCP;

// What edit_page_draft reads from outside the page: another page, a Shared Component. Each checks the user's access.
public interface IPageEditSources
{
    Task<JsonArray> GetPageSectionsAsync(string pageId, CancellationToken cancellationToken);

    // Refuses a component that is not one of the page's store the user may read.
    Task EnsureSharedComponentAsync(string sharedComponentId, CancellationToken cancellationToken);

    Task<JsonArray> GetSharedComponentSectionsAsync(string sharedComponentId, CancellationToken cancellationToken);
}

// What a document is: a page, a template of the theme, or the theme's header or footer. A template and a group hold
// sections as a page does, but no Shared Component (the storefront shows those on pages only), and a group has no
// settings of its own.
public enum PageDocumentKind
{
    Page,
    Template,
    SectionGroup,
}

// Applies the operations of edit_page_draft to a page document the way the designer's own edits change it (see the
// designer's useDesigner.ts and schema.ts): new items get new ids, a copy gets new ids for itself and its blocks, a
// placement of a Shared Component is never hidden or edited in place. Templates and the header and footer are edited
// with the same operations (edit_template, edit_theme_draft).
public sealed class PageDocumentEditor
{
    public static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Kept in step with the page's name, address and language (SiteBuilderContentSettings), which are changed with
    // the page itself rather than with its content.
    private static readonly string[] PageOwnedSettings = ["name", "permalink", "cultureName", TypeKey];
    private static readonly string[] ItemOwnedKeys = [IdKey, TypeKey, GroupKey, SharedComponents.ReferenceProperty];

    private readonly JsonObject _root;
    private readonly HashSet<string> _ids;
    private readonly PageDocumentKind _kind;

    private PageDocumentEditor(JsonObject root, SiteBuilderPageDocument content, PageDocumentKind kind)
    {
        _root = root;
        _kind = kind;
        Settings = content.Settings;
        Sections = content.Sections;
        IsLegacyShape = content.IsLegacyShape;
        _ids = CollectIds(Sections);
    }

    public JsonObject Settings { get; }

    public JsonArray Sections { get; }

    public bool IsLegacyShape { get; }

    // Null when the content is not a page document. A page without content starts empty, as in the designer.
    public static PageDocumentEditor Read(string content) => Read(content, PageDocumentKind.Page);

    public static PageDocumentEditor Read(string content, PageDocumentKind kind)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            content = DefaultPageContent;
        }

        var document = SiteBuilderPageDocument.Read(content);
        if (document == null)
        {
            return null;
        }

        return new PageDocumentEditor(document.IsLegacyShape ? null : (JsonObject)JsonNode.Parse(content), document, kind);
    }

    // What the document is called in a refusal, and where the agent reads its section ids.
    private string Noun => _kind switch
    {
        PageDocumentKind.Template => "template",
        PageDocumentKind.SectionGroup => GetString(_root?[TypeKey]) ?? "section group",
        _ => "page",
    };

    private string ReadHint => _kind switch
    {
        PageDocumentKind.Template => "Read the template with search_templates (template_key) for its section ids.",
        PageDocumentKind.SectionGroup => "Call edit_theme_draft with this part, dryRun and no operations for its section ids.",
        _ => "Read the page with search_site_pages (page_id) for its section ids.",
    };

    // In the shape it was read in, so a page stored the older way stays readable by what reads it that way.
    public string Write()
    {
        if (IsLegacyShape)
        {
            return new JsonArray([Settings.DeepClone(), .. Sections.Select(x => x?.DeepClone())]).ToJsonString(WriteOptions);
        }

        var root = (JsonObject)_root.DeepClone();
        // a header or footer file is { type, name, content }: it gets no settings it did not have
        if (_kind != PageDocumentKind.SectionGroup || root.ContainsKey("settings"))
        {
            root["settings"] = Settings.DeepClone();
        }

        root["content"] = Sections.DeepClone();
        return root.ToJsonString(WriteOptions);
    }

    public async Task ApplyAsync(IReadOnlyList<PageEditOperation> operations, IPageEditSources sources, CancellationToken cancellationToken)
    {
        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i] ?? throw new McpException($"Operation {i + 1} is empty.");
            try
            {
                await ApplyAsync(operation, sources, cancellationToken);
            }
            catch (EditException ex)
            {
                throw new McpException($"Operation {i + 1} ({operation.Op}): {ex.Message} Nothing was changed.");
            }
        }
    }

    private async Task ApplyAsync(PageEditOperation operation, IPageEditSources sources, CancellationToken cancellationToken)
    {
        switch (operation.Op?.Trim().ToLowerInvariant())
        {
            case "add_section":
                Insert(Sections, NewItem(operation, block: false), operation, PageEnd(), sectionList: true);
                break;
            case "duplicate_section":
                DuplicateSection(operation);
                break;
            case "move_section":
                Move(Sections, FindSection(operation.SectionId), operation, sectionList: true);
                break;
            case "hide_section":
                SetHidden(EditableSection(operation.SectionId, "be hidden"), hidden: true);
                break;
            case "show_section":
                SetHidden(EditableSection(operation.SectionId, "be hidden"), hidden: false);
                break;
            case "remove_section":
                Sections.Remove(FindSection(operation.SectionId));
                break;
            case "set_section_settings":
                SetSectionSettings(operation);
                break;
            case "add_block":
                AddBlock(operation);
                break;
            case "duplicate_block":
                DuplicateBlock(operation);
                break;
            case "move_block":
                MoveBlock(operation);
                break;
            case "hide_block":
                SetHidden(FindBlock(operation).Block, hidden: true);
                break;
            case "show_block":
                SetHidden(FindBlock(operation).Block, hidden: false);
                break;
            case "remove_block":
                RemoveBlock(operation);
                break;
            case "set_page_settings":
                if (_kind == PageDocumentKind.SectionGroup)
                {
                    throw new EditException($"The {Noun} has no settings of its own; set the settings of one of its sections with set_section_settings.");
                }

                SetValues(Settings, operation.Settings, operation.Language, page: _kind == PageDocumentKind.Page);
                break;
            case "copy_sections_from":
                await CopySectionsAsync(operation, sources, cancellationToken);
                break;
            case "place_shared_component":
                await PlaceSharedComponentAsync(operation, sources, cancellationToken);
                break;
            case "detach_shared_component":
                await DetachSharedComponentAsync(operation, sources, cancellationToken);
                break;
            default:
                throw new EditException($"'{operation.Op}' is not an operation. Use add_section, duplicate_section, move_section, hide_section, " +
                    "show_section, remove_section, set_section_settings, add_block, duplicate_block, move_block, hide_block, show_block, " +
                    "remove_block, set_page_settings, copy_sections_from, place_shared_component or detach_shared_component.");
        }
    }

    private JsonObject NewItem(PageEditOperation operation, bool block)
    {
        if (string.IsNullOrWhiteSpace(operation.Type))
        {
            throw new EditException($"Pass the type of the {(block ? "block" : "section")} to add.");
        }

        var type = operation.Type.Trim();
        if (type == SharedComponents.ReferenceType)
        {
            throw new EditException("A Shared Component is placed with place_shared_component.");
        }

        var item = new JsonObject { [IdKey] = NewId(type, _ids), [TypeKey] = type };
        if (operation.Settings?.Count > 0)
        {
            SetValues(item, operation.Settings, operation.Language, page: false);
        }

        return item;
    }

    private void DuplicateSection(PageEditOperation operation)
    {
        var section = FindSection(operation.SectionId);
        Sections.Insert(Sections.IndexOf(section) + 1, Copy(section, _ids));
    }

    private void SetSectionSettings(PageEditOperation operation)
    {
        var target = string.IsNullOrWhiteSpace(operation.BlockId)
            ? EditableSection(operation.SectionId, "have its own settings; change the component with revise_shared_component or detach it")
            : FindBlock(operation).Block;

        SetValues(target, operation.Settings, operation.Language, page: false);
    }

    private void AddBlock(PageEditOperation operation)
    {
        var section = EditableSection(operation.SectionId, "hold blocks of its own");
        if (GetBlocks(section) is not { } blocks)
        {
            blocks = [];
            section[BlocksKey] = blocks;
        }

        Insert(blocks, NewItem(operation, block: true), operation, blocks.Count, sectionList: false);
    }

    private void DuplicateBlock(PageEditOperation operation)
    {
        var (blocks, block) = FindBlock(operation);
        var copy = (JsonObject)block.DeepClone();
        copy[IdKey] = NewId(GetItemType(block), _ids);
        blocks.Insert(blocks.IndexOf(block) + 1, copy);
    }

    private void RemoveBlock(PageEditOperation operation)
    {
        var (blocks, block) = FindBlock(operation);
        blocks.Remove(block);
    }

    private void MoveBlock(PageEditOperation operation)
    {
        var (blocks, block) = FindBlock(operation);
        Move(blocks, block, operation, sectionList: false);
    }

    private async Task CopySectionsAsync(PageEditOperation operation, IPageEditSources sources, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operation.SourcePageId))
        {
            throw new EditException("Pass source_page_id, the page to copy sections from.");
        }

        var source = (await sources.GetPageSectionsAsync(operation.SourcePageId, cancellationToken)).OfType<JsonObject>().Where(x => !IsInGroup(x)).ToList();
        var picked = source;
        if (operation.SectionIds?.Count > 0)
        {
            var missing = operation.SectionIds.Where(id => source.All(x => GetId(x) != id)).ToList();
            if (missing.Count > 0)
            {
                throw new EditException($"Page '{operation.SourcePageId}' has no section {string.Join(", ", missing.Select(x => $"'{x}'"))}.");
            }

            picked = source.Where(x => operation.SectionIds.Contains(GetId(x))).ToList();
        }

        if (picked.Count == 0)
        {
            throw new EditException($"Page '{operation.SourcePageId}' has no sections to copy.");
        }

        if (picked.Any(IsPlacement))
        {
            EnsureCanHoldPlacements();
        }

        var index = GetInsertIndex(Sections, operation, PageEnd(), sectionList: true);
        foreach (var copy in picked.Select(x => Copy(x, _ids)))
        {
            Sections.Insert(index++, copy);
        }
    }

    private async Task PlaceSharedComponentAsync(PageEditOperation operation, IPageEditSources sources, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operation.SharedComponentId))
        {
            throw new EditException("Pass shared_component_id; search_shared_components finds it.");
        }

        EnsureCanHoldPlacements();
        await sources.EnsureSharedComponentAsync(operation.SharedComponentId, cancellationToken);

        var placement = new JsonObject
        {
            [IdKey] = NewPlacementId(_ids),
            [TypeKey] = SharedComponents.ReferenceType,
            [SharedComponents.ReferenceProperty] = operation.SharedComponentId,
        };
        Insert(Sections, placement, operation, PageEnd(), sectionList: true);
    }

    // The component's sections take the placement's place as the page's own copies; the component stays as it is.
    private async Task DetachSharedComponentAsync(PageEditOperation operation, IPageEditSources sources, CancellationToken cancellationToken)
    {
        var placement = FindSection(operation.SectionId);
        if (!IsPlacement(placement))
        {
            throw new EditException($"Section '{operation.SectionId}' is not a placed Shared Component.");
        }

        var sections = await sources.GetSharedComponentSectionsAsync(GetComponentId(placement), cancellationToken);
        var index = Sections.IndexOf(placement);
        Sections.RemoveAt(index);
        foreach (var copy in sections.OfType<JsonObject>().Where(x => !IsPlacement(x)).Select(x => Copy(x, _ids)))
        {
            Sections.Insert(index++, copy);
        }
    }

    private void EnsureCanHoldPlacements()
    {
        if (_kind != PageDocumentKind.Page)
        {
            throw new EditException($"A {Noun} cannot hold a Shared Component: the storefront shows Shared Components on pages only.");
        }

        if (IsLegacyShape)
        {
            throw new EditException("This page is stored in the older format, which cannot hold a Shared Component.");
        }
    }

    private JsonObject FindSection(string sectionId)
    {
        if (string.IsNullOrWhiteSpace(sectionId))
        {
            throw new EditException("Pass section_id.");
        }

        var section = Sections.OfType<JsonObject>().FirstOrDefault(x => GetId(x) == sectionId)
            ?? throw new EditException($"The {Noun} has no section '{sectionId}'. {ReadHint}");

        if (IsInGroup(section))
        {
            throw new EditException($"Section '{sectionId}' belongs to the theme's header or footer, not to the {Noun}; edit it with edit_theme_draft.");
        }

        return section;
    }

    private JsonObject EditableSection(string sectionId, string refusal)
    {
        var section = FindSection(sectionId);
        if (IsPlacement(section))
        {
            throw new EditException($"Section '{sectionId}' is a placed Shared Component, which cannot {refusal}.");
        }

        return section;
    }

    private (JsonArray Blocks, JsonObject Block) FindBlock(PageEditOperation operation)
    {
        var section = EditableSection(operation.SectionId, "hold blocks of its own");
        if (string.IsNullOrWhiteSpace(operation.BlockId))
        {
            throw new EditException("Pass block_id.");
        }

        var blocks = GetBlocks(section);
        var block = blocks?.OfType<JsonObject>().FirstOrDefault(x => GetId(x) == operation.BlockId)
            ?? throw new EditException($"Section '{operation.SectionId}' has no block '{operation.BlockId}'.");

        return (blocks, block);
    }

    // The designer's pageEnd: before the footer when the document holds the theme's sections too.
    private int PageEnd()
    {
        var footer = Sections.Select((x, i) => (x, i)).FirstOrDefault(x => x.x is JsonObject item && GetString(item[GroupKey]) == "footer");
        return footer.x == null ? Sections.Count : footer.i;
    }

    private static void SetHidden(JsonObject item, bool hidden)
    {
        if (hidden)
        {
            item[HiddenKey] = true;
        }
        else if (item.ContainsKey(HiddenKey))
        {
            item[HiddenKey] = false;
        }
    }

    private void Insert(JsonArray list, JsonObject item, PageEditOperation operation, int end, bool sectionList)
    {
        list.Insert(GetInsertIndex(list, operation, end, sectionList), item);
    }

    private void Move(JsonArray list, JsonObject item, PageEditOperation operation, bool sectionList)
    {
        if (operation.Position == null && string.IsNullOrWhiteSpace(operation.After) && string.IsNullOrWhiteSpace(operation.Before))
        {
            throw new EditException("Pass position, after or before: where the item goes.");
        }

        var id = GetId(item);
        if (operation.After == id || operation.Before == id)
        {
            throw new EditException("An item cannot go next to itself; name another one in after or before.");
        }

        list.Remove(item);
        list.Insert(GetInsertIndex(list, operation, list.Count, sectionList), item);
    }

    private int GetInsertIndex(JsonArray list, PageEditOperation operation, int end, bool sectionList)
    {
        var named = new[] { operation.Position != null, !string.IsNullOrWhiteSpace(operation.After), !string.IsNullOrWhiteSpace(operation.Before) }.Count(x => x);
        if (named > 1)
        {
            throw new EditException("Pass only one of position, after and before.");
        }

        var what = sectionList ? "section" : "block";
        if (operation.Position is { } position)
        {
            if (position < 0 || position > list.Count)
            {
                throw new EditException($"position {position} is outside 0..{list.Count}.");
            }

            return position;
        }

        var anchor = operation.After ?? operation.Before;
        if (string.IsNullOrWhiteSpace(anchor))
        {
            return end;
        }

        var index = list.Select((x, i) => (x, i)).FirstOrDefault(x => x.x is JsonObject item && GetId(item) == anchor && !(sectionList && IsInGroup(item)));
        if (index.x == null)
        {
            throw new EditException($"There is no {what} '{anchor}' to place it next to.");
        }

        return string.IsNullOrWhiteSpace(operation.After) ? index.i : index.i + 1;
    }

    private static void SetValues(JsonObject target, Dictionary<string, JsonNode> values, string language, bool page)
    {
        if (values == null || values.Count == 0)
        {
            throw new EditException("Pass settings: the values to set.");
        }

        if (language != null && !IsLanguageCode(language))
        {
            throw new EditException($"'{language}' is not a language code such as en-US.");
        }

        foreach (var (path, value) in values)
        {
            var segments = (path ?? string.Empty).Split('.');
            if (segments.Any(string.IsNullOrWhiteSpace))
            {
                throw new EditException($"'{path}' is not a setting path such as 'title' or 'button.url'.");
            }

            if (page)
            {
                CheckPageKey(segments[0]);
                SetValue(target, segments, value?.DeepClone(), language, path);
            }
            else
            {
                SetItemValue(target, segments, value?.DeepClone(), language, path);
            }
        }
    }

    private static void CheckPageKey(string key)
    {
        if (PageOwnedSettings.Contains(key))
        {
            throw new EditException($"'{key}' follows the page's own name, address and language, which edit_page_draft does not change.");
        }
    }

    private static void SetItemValue(JsonObject item, string[] segments, JsonNode value, string language, string path)
    {
        var key = segments[0];
        if (ItemOwnedKeys.Contains(key))
        {
            throw new EditException($"'{key}' identifies the item and cannot be set.");
        }

        if (key == HiddenKey)
        {
            throw new EditException("Hide or show an item with hide_section, show_section, hide_block or show_block.");
        }

        if (key != BlocksKey)
        {
            SetValue(item, segments, value, language, path);
            return;
        }

        if (segments.Length < 3)
        {
            throw new EditException("Blocks are added, moved and removed with their own operations; set a value of a block with " +
                "'blocks.<block id>.<setting>' or block_id.");
        }

        var block = FindListItem(GetBlocks(item), segments[1]) as JsonObject
            ?? throw new EditException($"There is no block '{segments[1]}' for '{path}'.");
        SetItemValue(block, segments[2..], value, language, path);
    }

    private static void SetValue(JsonObject target, string[] segments, JsonNode value, string language, string path)
    {
        JsonNode parent = target;
        foreach (var segment in segments[..^1])
        {
            var child = GetChild(parent, segment);
            if (child == null)
            {
                if (value == null)
                {
                    return;
                }

                if (parent is not JsonObject parentObject)
                {
                    throw new EditException($"The list in '{path}' has no item '{segment}'.");
                }

                child = new JsonObject();
                parentObject[segment] = child;
            }

            if (child is JsonValue)
            {
                throw new EditException($"'{path}' goes through '{segment}', which holds a value rather than settings.");
            }

            parent = child;
        }

        var last = segments[^1];
        if (parent is JsonArray list)
        {
            var index = FindListIndex(list, last);
            if (index < 0)
            {
                throw new EditException($"The list in '{path}' has no item '{last}'.");
            }

            if (value == null)
            {
                throw new EditException($"'{path}' is an item of a list; to remove it, set the whole list.");
            }

            list[index] = SetText(list[index]?.DeepClone(), value, language, path);
            return;
        }

        var parentObjectNode = (JsonObject)parent;
        if (value == null && language == null)
        {
            parentObjectNode.Remove(last);
            return;
        }

        parentObjectNode[last] = SetText(parentObjectNode[last]?.DeepClone(), value, language, path);
    }

    // A text per language changes in one language at a time, so another language's text is never lost to a plain value.
    private static JsonNode SetText(JsonNode current, JsonNode value, string language, string path)
    {
        if (language == null)
        {
            if (IsTranslations(current) && value is JsonValue)
            {
                throw new EditException($"'{path}' holds a text per language ({string.Join(", ", ((JsonObject)current).Select(x => x.Key))}); " +
                    "pass language to set one of them.");
            }

            return value;
        }

        if (!IsTranslations(current))
        {
            throw new EditException($"'{path}' holds one text for every language, so language does not apply; set it without language.");
        }

        var translations = (JsonObject)current;
        var key = translations.Select(x => x.Key).FirstOrDefault(x => x.Equals(language, StringComparison.OrdinalIgnoreCase)) ?? language;
        if (value == null)
        {
            translations.Remove(key);
        }
        else if (GetString(value) is { } text)
        {
            translations[key] = text;
        }
        else
        {
            throw new EditException($"The {language} text of '{path}' must be a string.");
        }

        return translations;
    }

    private static JsonNode GetChild(JsonNode parent, string segment) => parent switch
    {
        JsonObject value => value[segment],
        JsonArray list => FindListItem(list, segment),
        _ => null,
    };

    private static JsonNode FindListItem(JsonArray list, string segment)
    {
        var index = list == null ? -1 : FindListIndex(list, segment);
        return index < 0 ? null : list[index];
    }

    // An item of a list by its id, else by its 0-based index.
    private static int FindListIndex(JsonArray list, string segment)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is JsonObject item && GetId(item) == segment)
            {
                return i;
            }
        }

        return int.TryParse(segment, out var index) && index >= 0 && index < list.Count ? index : -1;
    }

    private sealed class EditException(string message) : Exception(message);
}
