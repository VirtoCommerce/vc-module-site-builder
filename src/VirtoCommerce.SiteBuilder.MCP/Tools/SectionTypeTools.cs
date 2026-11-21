using System.ComponentModel;
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
    ISchemaCatalogService schemaCatalogService)
{
    [McpServerTool(Name = "save_section_type", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [Authorize(Theme)]
    [Description("Create or replace a custom section type of a store's theme: a section the storefront renders from an HTML " +
        "template, its CSS and a settings schema, with no release. Read site-builder://stores/{store_id}/section-kit first: the " +
        "elements and bindings a template may use, the setting types, the CSS rules, the store's palette tokens, the kit tags " +
        "and behaviors (such as data-inview or data-count-up) and a complete example. Keep every text a customer reads in a " +
        "translatable setting bound with {{ settings.<id> }}, and take colors from the palette (var(--color-…)) or a " +
        "color-token setting. Call it with dryRun first: the answer lists the errors that refuse the type, the warnings and " +
        "the changes; fix them and call again until there are no errors. The save writes the theme's draft and needs no " +
        "approval. Then place the type with edit_page_draft (add_section with the type and its settings); each placement has " +
        "its own settings. Customers see it after publish_theme, which a second person approves, and then publish_page. Read " +
        "a type again at site-builder://stores/{store_id}/custom-sections/{type}; site-builder://stores/{store_id}/schemas " +
        "lists the theme's types, marked custom.")]
    public async Task<ThemeFileChange> SaveSectionType(
        RequestContext<CallToolRequestParams> context,
        [Description("Store id.")] string store_id,
        [Description("The type: 'custom-' and lowercase words joined by '-', at most 64 characters, for example 'custom-stats-band'. " +
            "A type the theme has is replaced; keep the type of one a page places.")] string type,
        [Description("The name in the designer's section picker: a text, or a text per language such as { \"en-US\": \"Stats band\" }.")] JsonNode name,
        [Description("The settings schema: { \"settings\": [fields], \"targetTemplates\"?: [template keys], \"limit\"?: number, \"icon\"?: text }.")] JsonObject schema,
        [Description("The HTML template: one root element, the section's root, with the settings bound into it.")] string template,
        [Description("The CSS, which reaches only the type's sections; :scope is the section's root. Omit it for none.")] string css = null,
        [Description("The version token of the type, from version_tokens of the last answer or from its resource; when given, a type " +
            "changed since is refused.")] string version_token = null,
        [Description("true to check the type and return its errors, its warnings and what it would change, without saving it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(store_id))
        {
            throw new McpException("Pass store_id: a section type belongs to a store's theme.");
        }

        await authorizationService.EnsureStoreAllowedAsync(context.User, store_id);

        var key = type?.Trim();
        if (string.IsNullOrEmpty(key) || !ThemeStorage.IsCustomSectionType(key))
        {
            throw new McpException("type must be 'custom-' and lowercase words joined by '-', at most 64 characters, for example 'custom-stats-band'.");
        }

        if (name == null || schema == null || template == null)
        {
            throw new McpException("Pass name, schema and template: the call saves the whole type.");
        }

        var resource = $"site-builder://stores/{store_id}/custom-sections/{key}";
        var path = await ThemeFiles.GetPathAsync(themeStorageFactory, store_id, ThemeStorage.GetCustomSectionFile(key));
        var current = await ThemeFiles.ReadAsync(designerOperationService, context.User, store_id, path, cancellationToken);
        if (!string.IsNullOrWhiteSpace(version_token) && version_token.Trim() != current.ETag)
        {
            throw new McpException($"Section type '{key}' changed since you read it, so nothing was saved. Read it again at {resource} and make the change on that version.");
        }

        var content = new JsonObject
        {
            ["format"] = 1,
            ["name"] = name.DeepClone(),
            ["schema"] = schema.DeepClone(),
            ["template"] = template,
            ["css"] = css ?? string.Empty,
        }.ToJsonString(PageDocumentEditor.WriteOptions);

        var created = current.Content == null;
        var change = new ThemeFileChange
        {
            Action = created ? "create_section_type" : "save_section_type",
            ObjectType = "section-type",
            ObjectId = path,
            ObjectName = $"Section type '{key}'",
            StoreId = store_id,
        };

        var catalogHash = await ThemeFiles.RequireCatalogHashAsync(schemaCatalogService, store_id, cancellationToken);
        await ThemeFileEdits.SaveAsync(change, designerOperationService, context.User, store_id, catalogHash, [new ThemeFileEdit(path, current, content)],
            dryRun, Theme, $"Read it again at {resource} and repeat the save on that version.", answerErrors: true, cancellationToken);

        if (created && change.Errors == null)
        {
            change.AddWarning($"Place it with edit_page_draft (add_section, type '{key}', with its settings); a page shows it only once the theme is published.");
        }

        return change;
    }
}
