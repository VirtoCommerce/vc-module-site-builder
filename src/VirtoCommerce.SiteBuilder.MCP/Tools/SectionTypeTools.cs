using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.SiteBuilder.Core.Models;
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
    ISiteBuilderSectionScanner sectionScanner,
    Func<UserManager<ApplicationUser>> userManagerFactory)
{
    private const int MaxPlacingPages = 50;
    private const int MaxDefaultLength = 80;
    private static readonly string[] LabelFieldTypes = ["header", "paragraph"];

    private const string CreateAction = "create_section_type";
    private const string ChangeAction = "change_section_type";

    private const string TypeDescription = $"The type, matching {ThemeStorage.CustomSectionTypePattern}, for example 'custom-stats-band'.";
    private const string NameDescription = "The name in the designer's section picker: a text, or a text per language such as { \"en-US\": \"Stats band\" }.";
    private const string SchemaDescription = "The settings schema: { \"settings\": [fields], \"targetTemplates\"?: [keys of the catalog's templates, as the " +
        "schemas resource lists them], \"limit\"?: number, \"icon\"?: text }.";
    private const string TemplateDescription = "The HTML template: one root element, the section's root, with the settings bound into it.";
    private const string CssDescription = "The CSS, which reaches only the type's sections; :scope is the section's root. Omit it for none.";
    private const string DryRunDescription = "true to check the type and return its errors, its warnings and what it would change, without saving it.";

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

        var key = request.Type?.Trim();
        if (string.IsNullOrEmpty(key) || !ThemeStorage.IsCustomSectionType(key))
        {
            throw new McpException($"type is not a custom section type, which {ThemeCustomSections.DescribeType()}, " +
                "for example 'custom-stats-band'.");
        }

        if (request.Name == null || request.Schema == null || request.Template == null)
        {
            throw new McpException("Pass name, schema and template: the call saves the whole type.");
        }

        var resource = $"site-builder://stores/{storeId}/custom-sections/{key}";
        var path = await ThemeFiles.GetPathAsync(themeStorageFactory, storeId, ThemeStorage.GetCustomSectionFile(key));
        var current = await ThemeFiles.ReadAsync(designerOperationService, user, storeId, path, cancellationToken);
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

        var content = new JsonObject
        {
            [ThemeCustomSections.FormatKey] = ThemeCustomSections.Format,
            [ThemeCustomSections.NameKey] = request.Name.DeepClone(),
            [ThemeCustomSections.SchemaKey] = request.Schema.DeepClone(),
            [ThemeCustomSections.TemplateKey] = request.Template,
            [ThemeCustomSections.CssKey] = request.Css ?? string.Empty,
        }.ToJsonString(PageDocumentEditor.WriteOptions);

        var change = new ThemeFileChange
        {
            Action = request.Create ? CreateAction : ChangeAction,
            ObjectType = "section-type",
            ObjectId = path,
            ObjectName = $"Section type '{key}'",
            StoreId = storeId,
        };

        var catalogHash = await ThemeFiles.RequireCatalogHashAsync(schemaCatalogService, storeId, cancellationToken);
        await ThemeFileEdits.SaveAsync(change, designerOperationService, user, storeId, catalogHash, [new ThemeFileEdit(path, current, content)],
            dryRun, Theme, $"Read it again at {resource} and repeat the change on that version.", cancellationToken);

        if (change.Errors != null)
        {
            return change;
        }

        var steps = new List<string>();
        if (request.Create)
        {
            steps.Add($"Place it with edit_page_draft (add_section, type '{key}', with its settings).");
        }
        else
        {
            change.PlacedSections = await DescribePlacementsAsync(user, storeId, key, current.Content, request.Schema, cancellationToken);
            if (change.PlacedSections?.NewSettings.Count > 0 && change.PlacedSections.Pages.Count > 0)
            {
                steps.Add("The sections placed before show the new settings' defaults (placed_sections): set them per page with edit_page_draft " +
                    "(set_section_settings), or on every placement with update_sections_bulk.");
            }
        }

        steps.Add(change.NextStep);
        if (!dryRun)
        {
            steps.Add($"To change the type again, pass version_tokens['{path}'] of this answer as version_token.");
        }

        change.NextStep = string.Join(' ', steps.Where(x => x != null));

        return change;
    }

    // A placed section keeps the settings it holds: a new setting shows its default there, and a removed one is held but read by nothing.
    private async Task<SectionTypePlacements> DescribePlacementsAsync(ClaimsPrincipal user, string storeId, string type, string currentContent, JsonObject schema,
        CancellationToken cancellationToken)
    {
        var before = GetSettings(ParseSchema(currentContent));
        var after = GetSettings(schema);
        var added = after.Where(x => !before.ContainsKey(x.Key)).ToList();
        var removed = before.Keys.Where(x => !after.ContainsKey(x)).ToList();
        if (added.Count == 0 && removed.Count == 0)
        {
            return null;
        }

        var scan = await sectionScanner.ScanPagesAsync(new SiteBuilderSectionScanCriteria
        {
            Pages = new SiteBuilderPageSearchCriteria { StoreId = storeId, Take = MaxPlacingPages },
            MaxPages = MaxPlacingPages,
            SectionType = type,
            SharedComponentStoreIds = await authorizationService.GetStoreIdsAsync(user, userManagerFactory, storeId, SharedComponentsRead),
        }, cancellationToken);

        return new SectionTypePlacements
        {
            NewSettings = added.Select(x => new NewSettingRow(x.Key, DescribeDefault(x.Value))).ToList(),
            RemovedSettings = removed,
            Pages = scan.Pages
                .Where(x => x.Sections.Count > 0)
                .GroupBy(x => x.ListItem.Id, StringComparer.Ordinal)
                .Select(x => new PlacingPageRow(x.Key, x.First().ListItem.Name, x.First().ListItem.Permalink, x.Sum(y => y.Sections.Count)))
                .ToList(),
            Coverage = SectionScanCoverage.Describe(scan) + " Templates are not scanned: search_templates lists them.",
            Note = removed.Count > 0
                ? "A removed setting stays in the placed sections, but the type no longer reads it: what pages hold in it is no longer shown."
                : null,
        };
    }

    private static Dictionary<string, JsonNode> GetSettings(JsonObject schema) =>
        (schema?["settings"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(x => x["id"] is JsonValue && !LabelFieldTypes.Contains(x["type"]?.GetValue<string>(), StringComparer.Ordinal))
            .GroupBy(x => x["id"]!.ToString(), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First()["default"], StringComparer.Ordinal);

    private static JsonObject ParseSchema(string content)
    {
        try
        {
            return (JsonNode.Parse(content ?? "") as JsonObject)?[ThemeCustomSections.SchemaKey] as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string DescribeDefault(JsonNode value) => value switch
    {
        null => "nothing: the setting has no default",
        JsonArray rows => $"its default, {rows.Count} rows",
        JsonValue text when text.TryGetValue<string>(out var str) => string.IsNullOrEmpty(str)
            ? "nothing: its default is empty"
            : $"its default '{(str.Length > MaxDefaultLength ? str[..MaxDefaultLength] + "…" : str)}'",
        _ => $"its default {value.ToJsonString()}",
    };

    private sealed record SectionTypeRequest(string StoreId, string Type, JsonNode Name, JsonObject Schema, string Template, string Css, string VersionToken,
        bool Create);
}
