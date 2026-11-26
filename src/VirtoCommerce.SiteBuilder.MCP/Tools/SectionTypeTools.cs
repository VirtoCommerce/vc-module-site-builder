using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.MCP.Extensions;
using VirtoCommerce.SiteBuilder.MCP.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class SectionTypeTools(
    IAuthorizationService authorizationService,
    IDesignerOperationService designerOperationService,
    IThemeStorageFactory themeStorageFactory,
    ISchemaCatalogService schemaCatalogService,
    SectionTypePages sectionTypePages)
{
    private const string CreateAction = "create_section_type";
    private const string ChangeAction = "change_section_type";

    private const string TypeDescription = $"The type, matching {ThemeStorage.CustomSectionTypePattern}, for example 'custom-stats-band'.";
    private const string NameDescription = "The name in the designer's section picker: a text, or a text per language such as { \"en-US\": \"Stats band\" }.";
    private const string SchemaDescription = "The settings schema: { \"settings\": [fields], \"targetTemplates\"?: [keys of the catalog's templates, as the " +
        "schemas resource lists them], \"limit\"?: number, \"icon\"?: text }.";
    private const string TemplateDescription = "The HTML template: one root element, the section's root, with the settings bound into it.";
    private const string CssDescription = "The CSS, which reaches only the type's sections; :scope is the section's root. Omit it for none.";
    private const string DryRunDescription = "true to check the type and return its errors, its warnings and what it would change, without saving it.";

    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
        Justification = "Each parameter is an argument of the MCP tool's input schema.")]
    [McpServerTool(Name = CreateAction, ReadOnly = false, Destructive = false, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [Authorize(Theme)]
    [Description("Create a custom section type in a store's theme: a section the storefront renders from an HTML template, its CSS " +
        "and a settings schema, with no release. Use it when no section does what is asked: if a built-in section or a custom type " +
        "of the theme does it already (site-builder://stores/{store_id}/schemas lists them, the custom ones marked custom), place " +
        "that with edit_page_draft, or change the custom type with change_section_type. Read site-builder://stores/{store_id}/section-kit " +
        "first: the elements and bindings a template may use, the setting types, the CSS rules, the store's palette tokens, the kit " +
        "tags and behaviors (such as data-inview or data-count-up), the design rules to follow and a complete example. Keep every text a customer reads in a " +
        "translatable setting bound with {{ settings.<id> }}, and take colors from the palette (var(--color-…)) or a color-token " +
        "setting. Call it with dryRun first: the answer lists the errors that refuse the type, the warnings and the changes; fix " +
        "them and call again until there are no errors. It writes the theme's draft, needs no approval, and refuses a type the " +
        "theme has. Then place the type with edit_page_draft (add_section with the type and its settings); each placement has its " +
        "own settings. Customers see it after publish_theme, which a second person approves, and then publish_page.")]
    public Task<ThemeFileChange> CreateSectionType(
        RequestContext<CallToolRequestParams> context,
        [Description("Store id.")] string store_id,
        [Description(TypeDescription + " The theme must not have it yet.")] string type,
        [Description(NameDescription)] JsonNode name,
        [Description(SchemaDescription)] JsonObject schema,
        [Description(TemplateDescription)] string template,
        [Description(CssDescription)] string css = null,
        [Description(DryRunDescription)] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        SaveAsync(context.User, new SectionTypeRequest(store_id, type, name, schema, template, css, VersionToken: null, Create: true), dryRun, cancellationToken);

    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
        Justification = "Each parameter is an argument of the MCP tool's input schema.")]
    [McpServerTool(Name = ChangeAction, ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [Authorize(Theme)]
    [Description("Change a custom section type of a store's theme: its name, schema, template and CSS are replaced with the ones " +
        "passed. Every page and template that places the type changes with it once the theme is published; the pages keep their " +
        "settings, so a setting removed or renamed loses what pages hold in it. Read the type first at " +
        "site-builder://stores/{store_id}/custom-sections/{type} and pass its version_token; the rules and the design rules are in " +
        "site-builder://stores/{store_id}/section-kit. Call it with dryRun first: the answer lists the errors that refuse the type, " +
        "the warnings and the changes, setting by setting and line by line; fix them and call again until there are no errors. It " +
        "writes the theme's draft and needs no approval; customers see it after publish_theme, which a second person approves. A " +
        "type the theme does not have is refused: create it with create_section_type. To change what one page shows, change the " +
        "section's settings with edit_page_draft instead.")]
    public Task<ThemeFileChange> ChangeSectionType(
        RequestContext<CallToolRequestParams> context,
        [Description("Store id.")] string store_id,
        [Description(TypeDescription + " The theme must have it.")] string type,
        [Description(NameDescription)] JsonNode name,
        [Description(SchemaDescription)] JsonObject schema,
        [Description(TemplateDescription)] string template,
        [Description(CssDescription)] string css = null,
        [Description("The version token of the type, from its resource or from version_tokens of the last answer: a type changed since " +
            "is refused.")] string version_token = null,
        [Description(DryRunDescription)] bool dryRun = false,
        CancellationToken cancellationToken = default) =>
        SaveAsync(context.User, new SectionTypeRequest(store_id, type, name, schema, template, css, version_token, Create: false), dryRun, cancellationToken);

    private async Task<ThemeFileChange> SaveAsync(ClaimsPrincipal user, SectionTypeRequest request, bool dryRun, CancellationToken cancellationToken)
    {
        var storeId = request.StoreId;
        if (string.IsNullOrWhiteSpace(storeId))
        {
            throw new McpException("Pass store_id: a section type belongs to a store's theme.");
        }

        await authorizationService.EnsureStoreAllowedAsync(user, storeId);

        var key = GetSectionType(request);
        if (request.Name == null || request.Schema == null || request.Template == null)
        {
            throw new McpException("Pass name, schema and template: the call saves the whole type.");
        }

        var resource = $"site-builder://stores/{storeId}/custom-sections/{key}";
        var path = await ThemeFiles.GetPathAsync(themeStorageFactory, storeId, ThemeStorage.GetCustomSectionFile(key));
        var current = await ThemeFiles.ReadAsync(designerOperationService, user, storeId, path, cancellationToken);
        EnsureCanSave(request, current, key, path, resource);

        var content = GetContent(request);
        var change = new ThemeFileChange
        {
            Action = request.Create ? CreateAction : ChangeAction,
            ObjectType = "section-type",
            ObjectId = path,
            ObjectName = $"Section type '{key}'",
            StoreId = storeId,
        };

        var catalogHash = await ThemeFiles.RequireCatalogHashAsync(schemaCatalogService, storeId, cancellationToken);
        await ThemeFileEdits.SaveAsync(change, designerOperationService, user,
            new ThemeFileSaveTarget(storeId, catalogHash, dryRun, Theme, $"Read it again at {resource} and repeat the change on that version."),
            [new ThemeFileEdit(path, current, content)], cancellationToken);

        if (change.Errors != null)
        {
            return change;
        }

        if (!request.Create)
        {
            change.PlacedSections = await DescribePlacementsAsync(user, storeId, key, current.Content, request.Schema, cancellationToken);
        }

        change.NextStep = GetNextStep(change, request.Create, key, path, dryRun);

        return change;
    }

    private static string GetSectionType(SectionTypeRequest request)
    {
        var key = request.Type?.Trim();
        if (string.IsNullOrEmpty(key) || !ThemeStorage.IsCustomSectionType(key))
        {
            throw new McpException($"type is not a custom section type, which {ThemeCustomSections.DescribeType()}, " +
                "for example 'custom-stats-band'.");
        }

        return key;
    }

    private static void EnsureCanSave(SectionTypeRequest request, ThemeFileContent current, string key, string path, string resource)
    {
        if (request.Create && current.Content != null)
        {
            throw new McpException($"The theme has the custom section type '{key}' already, so nothing was saved. Change it with change_section_type " +
                $"(read it at {resource}), or create the new one under another type.");
        }

        if (!request.Create && current.Content == null)
        {
            throw new McpException($"The theme has no custom section type '{key}', so nothing was saved. Create it with create_section_type.");
        }

        if (!request.Create && string.IsNullOrWhiteSpace(request.VersionToken))
        {
            throw new McpException($"Pass version_token: the call replaces the whole type, so it has to be of the version you read. Read the type at {resource}, " +
                $"make the change on it, and pass its version_token; the version_tokens['{path}'] of your last create_section_type or change_section_type " +
                "answer for it is that token too.");
        }

        if (!string.IsNullOrWhiteSpace(request.VersionToken) && request.VersionToken.Trim() != current.ETag)
        {
            throw new McpException($"Section type '{key}' changed since you read it, so nothing was saved. Read it again at {resource} and make the change on that version.");
        }
    }

    private static string GetContent(SectionTypeRequest request) => new JsonObject
    {
        [ThemeCustomSections.FormatKey] = ThemeCustomSections.Format,
        [ThemeCustomSections.NameKey] = request.Name.DeepClone(),
        [ThemeCustomSections.SchemaKey] = request.Schema.DeepClone(),
        [ThemeCustomSections.TemplateKey] = request.Template,
        [ThemeCustomSections.CssKey] = request.Css ?? string.Empty,
    }.ToJsonString(PageDocumentEditor.WriteOptions);

    private static string GetNextStep(ThemeFileChange change, bool create, string key, string path, bool dryRun)
    {
        var steps = new List<string>();
        if (dryRun && change.Changes.Count > 0)
        {
            steps.Add($"Call {change.Action} again without dryRun to save the type.");
        }

        if (create)
        {
            steps.Add($"Place it with edit_page_draft (add_section, type '{key}', with its settings).");
        }
        else if (GetPlacedSectionsStep(change.PlacedSections) is { } placedSectionsStep)
        {
            steps.Add(placedSectionsStep);
        }

        if (change.NextStep != null)
        {
            steps.Add(change.NextStep);
        }

        if (!dryRun)
        {
            steps.Add($"To change the type again, pass version_tokens['{path}'] of this answer as version_token.");
        }

        return steps.Count > 0 ? string.Join(' ', steps) : null;
    }

    private static string GetPlacedSectionsStep(SectionTypePlacements placedSections)
    {
        if (!(placedSections?.NewSettings.Count > 0 && placedSections.Pages.Count > 0))
        {
            return null;
        }

        return "The sections placed before show the new settings' defaults (placed_sections): set them per page with edit_page_draft " +
            "(set_section_settings), or on every placement with update_sections_bulk" +
            (placedSections.Pages.Any(x => x.SharedComponentIds.Count > 0)
                ? "; those of a placed Shared Component (shared_component_ids) on the component, with change_shared_component."
                : ".");
    }

    private async Task<SectionTypePlacements> DescribePlacementsAsync(ClaimsPrincipal user, string storeId, string type, string currentContent, JsonObject schema,
        CancellationToken cancellationToken)
    {
        var settings = SectionTypeSettingChanges.Compare(SectionTypeSettingChanges.ReadSchema(currentContent), schema);
        if (settings.IsEmpty)
        {
            return null;
        }

        var (pages, coverage) = await sectionTypePages.FindAsync(user, storeId, type, published: false, cancellationToken);

        return settings.ToPlacements(pages, coverage);
    }

    private sealed record SectionTypeRequest(string StoreId, string Type, JsonNode Name, JsonObject Schema, string Template, string Css, string VersionToken,
        bool Create);
}
