using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// The designer's templates of a store's theme: what there is, which rules give each its pages, and edits of a template's
// draft. A template draft reaches customers only with the theme's publication, so an edit needs no second person; the
// save is the designer's own, with its checks (the catalog, the main section) and history.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class TemplateTools(
    IAuthorizationService authorizationService,
    ISiteBuilderContentQueryService contentQueryService,
    IDesignerOperationService designerOperationService,
    IThemeOperationService themeOperationService,
    ISiteBuilderSectionScanner sectionScanner,
    IThemeStorageFactory themeStorageFactory,
    ISchemaCatalogService schemaCatalogService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService pageContentService,
    ISiteBuilderSharedComponentService sharedComponentService,
    ISiteBuilderSharedComponentContentService sharedComponentContentService)
{
    private const int MaxOperations = 100;

    private static readonly string[] Layouts = ["full", "minimal", "blank"];

    [McpServerTool(Name = "search_templates", ReadOnly = true, Destructive = false, Idempotent = true)]
    [McpPermission(Read)]
    [Description("List the templates of a store's theme: key, page type (product, category, page), whether it is the type's " +
        "default or an alternate one, whether it is published, whether it has an unpublished draft, and the template rules that " +
        "give it pages. The answer also lists every rule in the order the storefront tries them, with rules_version_token for " +
        "edit_template_rules. Pass section_type to find the templates that use a section. Pass template_key to read one " +
        "template: its settings (layout), its sections with their ids and settings, and the version_token edit_template needs. " +
        "For pages use search_site_pages; for which template a page gets, get_preview.")]
    public async Task<TemplateSearch> SearchTemplates(
        RequestContext<CallToolRequestParams> context,
        IMcpPageSizeResolver pageSizeResolver,
        [Description("Store id.")] string store_id,
        [Description("Read one template, for example 'product' or 'product.bicycles'.")] string template_key = null,
        [Description("Only templates of this page type: product, category or page.")] string page_type = null,
        [Description("Only templates that hold a section of this type, for example 'product-reviews'.")] string section_type = null,
        [Description("With template_key or section_type: 'draft' (default) for the templates as the designer edits them, " +
            "'published' for what customers see.")] string version = null,
        [Description("Rows to skip, from 0. Use next_skip of the previous page.")] int? skip = null,
        [Description("Rows to return. Omit it for the default page size.")] int? take = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureStoreAsync(context.User, store_id);
        var published = SitePageFilters.ParseVersion(version);
        var type = ParsePageType(page_type);

        var templates = await ThemeTemplates.ListAsync(contentQueryService, context.User, store_id, cancellationToken);
        var (rules, rulesFile) = await ThemeTemplates.ReadRulesAsync(designerOperationService, themeStorageFactory, context.User, store_id, cancellationToken);
        var status = await themeOperationService.GetStatusAsync(new SiteBuilderOperationRequest { StoreId = store_id }, context.User, cancellationToken);
        var rows = templates
            .Where(x => type == null || x.Type == type)
            .Select(x => TemplateRow.From(x, rules))
            .ToList();

        var answer = new TemplateSearch
        {
            StoreId = store_id,
            Rules = TemplateRuleRow.From(rules),
            RulesVersionToken = rulesFile.ETag,
            RulesHaveDraft = status.Value?.Drafts.Contains(ThemeStorage.TemplateRulesFile, StringComparer.OrdinalIgnoreCase) == true,
        };
        if (rules.Unreadable > 0)
        {
            answer.Notes.Add($"The rules file holds {rules.Unreadable} rule(s) the storefront cannot read; they are left out here and dropped by the next rules edit.");
        }

        if (!string.IsNullOrWhiteSpace(template_key))
        {
            var key = template_key.Trim();
            var row = rows.FirstOrDefault(x => x.Key == key)
                ?? throw new McpException($"The theme of store '{store_id}' has no template '{key}'. Call search_templates without template_key for its templates.");
            answer.Template = await ReadTemplateAsync(context.User, store_id, row, published, cancellationToken);
            rows = [row];
        }

        if (!string.IsNullOrWhiteSpace(section_type))
        {
            var scan = await sectionScanner.ScanTemplatesAsync(new SiteBuilderTemplateSectionScanCriteria
            {
                StoreId = store_id,
                Published = published,
                SectionType = section_type.Trim(),
            }, cancellationToken);

            var matches = scan.Templates.ToDictionary(x => x.Key, StringComparer.Ordinal);
            rows = rows.Where(x => matches.ContainsKey(x.Key)).ToList();
            foreach (var row in rows)
            {
                row.Sections = matches[row.Key].Sections.Select(SectionMatchRow.From).ToList();
            }

            answer.Notes.Add($"Every one of the theme's {scan.TemplateCount} templates was read ({(published ? "published" : "drafts, else published")}).");
            answer.UnreadTemplateKeys = scan.UnreadTemplateKeys;
        }

        var paging = await pageSizeResolver.GetPagingAsync(skip, take);
        var page = McpPage<TemplateRow>.FromList(rows, paging);
        answer.Items = page.Items;
        answer.TotalCount = page.TotalCount;
        answer.Skip = page.Skip;
        answer.Take = page.Take;
        answer.NextSkip = page.NextSkip;

        return answer;
    }

    [McpServerTool(Name = "edit_template", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [Authorize(Templates)]
    [Description("Edit the draft of a template of a store's theme, or create a template as a copy of another one. Sections and " +
        "blocks are edited with the operations of edit_page_draft (add, duplicate, move, hide, show, remove, set settings); " +
        "layout sets what surrounds the pages of a page template. Read the template first with search_templates (template_key) " +
        "and pass its version_token. To create an alternate template, pass a new key such as 'product.bicycles' and copy_of, the " +
        "template to start from; then give it pages with edit_template_rules. The template's main section (product-info, " +
        "category-listing, page-content) cannot be removed or hidden. Customers see the change only when the theme is published " +
        "(publish_theme). Returns the changes section by section, the new version token and the sections. Call it with dryRun " +
        "first to show the change.")]
    public async Task<ThemeFileChange> EditTemplate(
        RequestContext<CallToolRequestParams> context,
        [Description("Store id.")] string store_id,
        [Description("The template, for example 'product' or 'product.bicycles'. A key the theme does not have creates the template; pass copy_of with it.")] string template_key,
        [Description("version_token of the template as search_templates (template_key) returned it. Not used when the template is created.")] string version_token = null,
        [Description("When the template is created: the template of the same page type to copy, for example 'product'.")] string copy_of = null,
        [Description("The changes of sections and blocks, applied in order, as in edit_page_draft. set_page_settings sets the template's settings.")] PageEditOperation[] operations = null,
        [Description("Page templates only: 'full' (the site's header and footer), 'minimal' (the logo only) or 'blank' (nothing around the page).")] string layout = null,
        [Description("true to check the edit and return what it would change, without saving it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureStoreAsync(context.User, store_id);
        var key = template_key?.Trim();
        var type = TemplateRulesDocument.PageTypeOf(key);
        if (key == null || type == null || !ThemeStorage.IsTemplateKey(key))
        {
            throw new McpException("template_key must be a template key: product, category or page, or one of them with an alternate name such as 'product.bicycles'.");
        }

        if ((operations?.Length ?? 0) > MaxOperations)
        {
            throw new McpException($"Pass at most {MaxOperations} operations in one call.");
        }

        if (layout != null && (type != "page" || !Layouts.Contains(layout)))
        {
            throw new McpException(type == "page"
                ? $"layout must be one of {string.Join(", ", Layouts)}."
                : "layout applies to page templates only: product and category pages always show the site's header and footer.");
        }

        var templates = await ThemeTemplates.ListAsync(contentQueryService, context.User, store_id, cancellationToken);
        var path = await ThemeFiles.GetPathAsync(themeStorageFactory, store_id, ThemeFiles.GetTemplateFile(key));
        var current = await ThemeFiles.ReadAsync(designerOperationService, context.User, store_id, path, cancellationToken);
        var exists = templates.Any(x => x.Key == key);
        var created = !exists;

        var editor = exists
            ? ReadExisting(key, current, version_token, copy_of)
            : await CreateAsync(context.User, store_id, key, type, templates, copy_of, cancellationToken);

        if (operations?.Length > 0)
        {
            var sources = new PageEditSources(authorizationService, groupedPageService, pageContentService, sharedComponentService,
                sharedComponentContentService, context.User, store_id);
            await editor.ApplyAsync(operations, sources, cancellationToken);
        }

        if (layout != null)
        {
            editor.Settings["layout"] = layout;
        }

        if (!created && (operations?.Length ?? 0) == 0 && layout == null)
        {
            throw new McpException("Pass operations or layout: the changes to make.");
        }

        var change = new ThemeFileChange
        {
            Action = created ? "create_template" : "edit_template",
            ObjectType = "template",
            ObjectId = path,
            ObjectName = $"Template '{key}'",
            StoreId = store_id,
        };

        var catalogHash = await ThemeFiles.RequireCatalogHashAsync(schemaCatalogService, store_id, cancellationToken);
        await ThemeFileEdits.SaveAsync(change, designerOperationService, context.User, store_id, catalogHash, [new ThemeFileEdit(path, current, editor.Write())],
            dryRun, Templates, $"Read it again with search_templates (template_key '{key}') and repeat the edit on that version.", cancellationToken);

        change.Sections = SectionRow.From(editor.Sections);
        if (created)
        {
            change.AddWarning($"Template '{key}' gets pages only through a template rule: add one with edit_template_rules (template '{key}').");
        }

        return change;
    }

    private static PageDocumentEditor ReadExisting(string key, ThemeFileContent current, string versionToken, string copyOf)
    {
        if (!string.IsNullOrWhiteSpace(copyOf))
        {
            throw new McpException($"Template '{key}' exists already; copy_of creates a template. Edit it without copy_of, or pass a new key.");
        }

        if (string.IsNullOrWhiteSpace(versionToken) || versionToken.Trim() != current.ETag)
        {
            throw new McpException($"Template '{key}' changed since you read it, or version_token is not its version. Read it again with " +
                $"search_templates (template_key '{key}') and make the edit on that content with its version_token.");
        }

        return PageDocumentEditor.Read(current.Content, PageDocumentKind.Template)
            ?? throw new McpException($"Template '{key}' is not a template document, so it cannot be edited here. Open it in the designer.");
    }

    // A copy of a template of the same type, as the designer starts one, without the copy's version and with the type's
    // main section; without copy_of the template starts with its main section only.
    private async Task<PageDocumentEditor> CreateAsync(ClaimsPrincipal user, string storeId, string key, string type, IList<ThemeTemplate> templates,
        string copyOf, CancellationToken cancellationToken)
    {
        var content = ThemeFileEdits.EmptyDocument(PageDocumentKind.Template);
        if (!string.IsNullOrWhiteSpace(copyOf))
        {
            var source = copyOf.Trim();
            if (TemplateRulesDocument.PageTypeOf(source) != type)
            {
                throw new McpException($"copy_of must be a {type} template, as '{key}' lays out {type} pages.");
            }

            if (templates.All(x => x.Key != source))
            {
                throw new McpException($"The theme has no template '{source}' to copy. Call search_templates for its templates.");
            }

            var sourcePath = await ThemeFiles.GetPathAsync(themeStorageFactory, storeId, ThemeFiles.GetTemplateFile(source));
            content = (await ThemeFiles.ReadAsync(designerOperationService, user, storeId, sourcePath, cancellationToken)).Content ?? content;
        }

        var root = JsonNode.Parse(content) as JsonObject;
        root?.Remove("version");
        var editor = PageDocumentEditor.Read(root?.ToJsonString(), PageDocumentKind.Template)
            ?? throw new McpException($"Template '{copyOf}' is not a template document, so it cannot be copied here.");

        var main = ThemeTemplateMainSections.GetMainSection(key);
        if (!editor.Sections.OfType<JsonObject>().Any(x => GetItemType(x) == main))
        {
            editor.Sections.Add(new JsonObject { [IdKey] = NewId(main, CollectIds(editor.Sections)), [TypeKey] = main });
        }

        if (type == "page" && editor.Settings["layout"] == null)
        {
            editor.Settings["layout"] = "full";
        }

        return editor;
    }

    private async Task<TemplateDetails> ReadTemplateAsync(ClaimsPrincipal user, string storeId, TemplateRow row, bool published, CancellationToken cancellationToken)
    {
        var path = await ThemeFiles.GetPathAsync(themeStorageFactory, storeId, ThemeFiles.GetTemplateFile(row.Key));
        string content;
        string versionToken = null;
        if (published)
        {
            content = await ThemeFiles.ReadLiveAsync(contentQueryService, user, storeId, path, cancellationToken);
        }
        else
        {
            var file = await ThemeFiles.ReadAsync(designerOperationService, user, storeId, path, cancellationToken);
            content = file.Content;
            versionToken = file.ETag;
        }

        var document = SiteBuilderPageDocument.Read(content);
        var main = ThemeTemplateMainSections.GetMainSection(row.Key);

        return new TemplateDetails
        {
            Key = row.Key,
            Path = path,
            Version = published ? SitePageFilters.PublishedVersion : SitePageFilters.DraftVersion,
            VersionToken = versionToken,
            Settings = document?.Settings,
            Sections = document?.Sections,
            MainSection = main,
            MainSectionState = document == null ? null : ThemeTemplateMainSections.GetState(Newtonsoft.Json.Linq.JToken.Parse(content), main)?.ToString(),
            Note = content == null
                ? "The template has no file in this version."
                : document == null ? "The stored template is not a template document." : null,
        };
    }

    private Task EnsureStoreAsync(ClaimsPrincipal user, string storeId)
    {
        if (string.IsNullOrWhiteSpace(storeId))
        {
            throw new McpException("Pass store_id: templates belong to a store's theme.");
        }

        return authorizationService.EnsureAllowedAsync(user, new SiteBuilderPageSearchCriteria { StoreId = storeId }, storeId);
    }

    private static string ParsePageType(string pageType)
    {
        if (string.IsNullOrWhiteSpace(pageType))
        {
            return null;
        }

        return TemplateRulesDocument.PageTypes.FirstOrDefault(x => x == pageType.Trim().ToLowerInvariant())
            ?? throw new McpException("page_type must be product, category or page.");
    }
}

// The templates and rules of a store's theme as the design tools read them.
public static class ThemeTemplates
{
    // The storefront's own templates, which it has whatever the theme holds: the default of each type, and the B2C
    // product template that its default product rule gives.
    public static readonly string[] BuiltIn = ["product", "product.b2c", "category", "page"];

    public static async Task<IList<ThemeTemplate>> ListAsync(ISiteBuilderContentQueryService contentQueryService, ClaimsPrincipal user, string storeId,
        CancellationToken cancellationToken)
    {
        var result = await contentQueryService.ListTemplatesAsync(new SiteBuilderThemeQueryRequest { StoreId = storeId }, user, cancellationToken);

        return result.Status switch
        {
            SiteBuilderOperationStatus.Succeeded => result.Value,
            SiteBuilderOperationStatus.Forbidden => throw new McpForbiddenException($"Your builder:read permission does not cover store '{storeId}'."),
            _ => throw new McpException($"The templates of store '{storeId}' could not be read: {result.Error}"),
        };
    }

    // The rules as the designer edits them: the draft, else the live file.
    public static async Task<(TemplateRulesDocument Rules, ThemeFileContent File)> ReadRulesAsync(IDesignerOperationService designerOperationService,
        IThemeStorageFactory themeStorageFactory, ClaimsPrincipal user, string storeId, CancellationToken cancellationToken)
    {
        var path = await ThemeFiles.GetPathAsync(themeStorageFactory, storeId, ThemeStorage.TemplateRulesFile);
        var file = await ThemeFiles.ReadAsync(designerOperationService, user, storeId, path, cancellationToken);
        var rules = TemplateRulesDocument.Read(file.Content)
            ?? throw new McpException($"The template rules of store '{storeId}' ({path}) are not a JSON object, so they cannot be read here. Fix them in the designer's rules panel.");

        return (rules, file);
    }
}

// search_templates' answer: a page of templates, and every rule of the theme with the version to edit them at.
public sealed class TemplateSearch
{
    public string StoreId { get; init; }

    public IList<TemplateRow> Items { get; set; } = [];

    public int TotalCount { get; set; }

    public int Skip { get; set; }

    public int Take { get; set; }

    public int? NextSkip { get; set; }

    public bool HasMore => NextSkip != null;

    // The rules as the designer edits them (the draft, else what is published), in the order the storefront tries them.
    public IList<TemplateRuleRow> Rules { get; init; } = [];

    // Pass it to edit_template_rules.
    public string RulesVersionToken { get; init; }

    public bool RulesHaveDraft { get; init; }

    // With template_key: the template itself.
    public TemplateDetails Template { get; set; }

    // With section_type: templates that could not be read as template documents.
    public IList<string> UnreadTemplateKeys { get; set; }

    public IList<string> Notes { get; set; } = [];
}

public sealed class TemplateRow
{
    public string Key { get; init; }

    public string PageType { get; init; }

    // The type's default template, which a page gets when no rule picks another.
    public bool IsDefault { get; init; }

    public bool Published { get; init; }

    // Unpublished changes; with published false the draft is the template's only copy.
    public bool HasDraft { get; init; }

    // The rules that give the template its pages, as "id" or "id (disabled)".
    public IList<string> Rules { get; init; } = [];

    // With section_type: the matching sections.
    [JsonPropertyName("sections")]
    public IList<SectionMatchRow> Sections { get; set; }

    public static TemplateRow From(ThemeTemplate template, TemplateRulesDocument rules) => new()
    {
        Key = template.Key,
        PageType = template.Type,
        IsDefault = template.IsDefault,
        Published = template.Published,
        HasDraft = template.HasDraft,
        Rules = rules.RulesOf(template.Type)
            .Where(x => TemplateRulesDocument.GetTemplate(x) == template.Key)
            .Select(x => TemplateRulesDocument.IsDisabled(x) ? $"{GetId(x)} (disabled)" : GetId(x))
            .ToList(),
    };
}

public sealed class TemplateDetails
{
    public string Key { get; init; }

    // The file with the theme folder, as list_versions and discard_draft take it.
    public string Path { get; init; }

    // draft or published: the version the content below is of.
    public string Version { get; init; }

    // Pass it to edit_template; only for the draft.
    public string VersionToken { get; init; }

    public JsonObject Settings { get; init; }

    public JsonArray Sections { get; init; }

    // The section that shows what the template's pages are about; it cannot be removed or hidden.
    public string MainSection { get; init; }

    // Shown, Hidden or Missing (the storefront then adds it itself).
    public string MainSectionState { get; init; }

    public string Note { get; init; }
}
