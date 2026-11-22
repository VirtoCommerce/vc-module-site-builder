using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.MCP.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Resources;

/// <summary>What an agent reads to write a custom section type, and the types of the theme.</summary>
[McpServerResourceType]
[McpScope(McpScopes.Admin)]
public class SectionTypeResources(
    IAuthorizationService authorizationService,
    ISchemaCatalogService schemaCatalogService,
    IThemeStorageFactory themeStorageFactory,
    IDesignerOperationService designerOperationService,
    IThemeOperationService themeOperationService,
    ICustomSectionValidator customSectionValidator)
{
    [McpServerResource(UriTemplate = "site-builder://stores/{store_id}/section-kit", Name = "section_kit", MimeType = SiteBuilderResources.JsonMimeType)]
    [McpPermission(Read)]
    [Description("Everything to write a custom section type with create_section_type or change_section_type: the elements and attributes a template may hold, " +
        "the bindings, the setting types, the setting variables, the CSS rules, the store's palette tokens, the kit tags and behaviors " +
        "the storefront offers, the storefront's own guide and design tokens, the design rules, the size limits and a complete example. " +
        "Read it before writing a type.")]
    public async Task<string> SectionKit(RequestContext<ReadResourceRequestParams> context, string store_id, CancellationToken cancellationToken)
    {
        var catalog = await ReadCatalogAsync(context.User, store_id, cancellationToken);
        var palette = AbstractTypeFactory<ThemePalette>.TryCreateInstance();
        palette.Light = await ReadPaletteColorsAsync(context.User, store_id, ThemeStorage.PaletteFile, cancellationToken);
        palette.Dark = await ReadPaletteColorsAsync(context.User, store_id, ThemeStorage.DarkPaletteFile, cancellationToken);

        return SiteBuilderResources.Serialize(SectionKitGuide.Describe(store_id, catalog, customSectionValidator.GetRules(), palette));
    }

    [McpServerResource(UriTemplate = SiteBuilderResources.SectionKitIconsUri, Name = "section_kit_icons", MimeType = SiteBuilderResources.JsonMimeType)]
    [McpPermission(Read)]
    [Description("Every icon name the storefront draws, as its catalog lists them: the values of a kit attribute whose field has " +
        "\"optionsFrom\": \"icons\", such as vc-icon's name or vc-button's prepend-icon. A name the list does not hold is refused.")]
    public async Task<string> SectionKitIcons(RequestContext<ReadResourceRequestParams> context, string store_id, CancellationToken cancellationToken)
    {
        var catalog = await ReadCatalogAsync(context.User, store_id, cancellationToken);
        if (catalog == null)
        {
            return SiteBuilderResources.Serialize(SiteBuilderResources.NoCatalog(store_id));
        }

        var icons = (catalog[CustomSectionKit.KitKey] as JsonObject)?[CustomSectionKit.IconsKey] as JsonArray;

        return SiteBuilderResources.Serialize(new
        {
            StoreId = store_id,
            Available = icons != null,
            Reason = icons == null ? "The storefront's catalog lists no icon names, so an icon name is not checked." : null,
            Icons = icons?.DeepClone(),
        });
    }

    // a palette file as its draft holds it, else as it is live; none for no file or one that is not a palette
    private async Task<IDictionary<string, string>> ReadPaletteColorsAsync(ClaimsPrincipal user, string storeId, string file, CancellationToken cancellationToken)
    {
        var path = await ThemeFiles.GetPathAsync(themeStorageFactory, storeId, file);
        var content = (await ThemeFiles.ReadAsync(designerOperationService, user, storeId, path, cancellationToken)).Content;
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        try
        {
            return ThemePaletteValues.ReadColors(ThemeJson.Parse(content));
        }
        catch (Newtonsoft.Json.JsonReaderException)
        {
            return null;
        }
    }

    [McpServerResource(UriTemplate = "site-builder://stores/{store_id}/custom-sections/{type}", Name = "custom_section", MimeType = SiteBuilderResources.JsonMimeType)]
    [McpPermission(Read)]
    [Description("A custom section type of the store's theme as change_section_type takes it: name, schema, template and css, as its draft " +
        "holds them, else as they are live, with its version_token and whether it is published. A type only in draft shows nothing " +
        "on the storefront until the theme is published.")]
    public async Task<string> CustomSection(RequestContext<ReadResourceRequestParams> context, string store_id, string type, CancellationToken cancellationToken)
    {
        await SiteBuilderResources.EnsureStoreAsync(authorizationService, context.User, store_id);
        if (string.IsNullOrEmpty(type) || !ThemeStorage.IsCustomSectionType(type))
        {
            return SiteBuilderResources.Serialize(new
            {
                StoreId = store_id,
                Available = false,
                Reason = $"'{type}' is not a custom section type, which {ThemeCustomSections.DescribeType()}.",
            });
        }

        var path = await ThemeFiles.GetPathAsync(themeStorageFactory, store_id, ThemeStorage.GetCustomSectionFile(type));
        var file = await ThemeFiles.ReadAsync(designerOperationService, context.User, store_id, path, cancellationToken);
        if (file.Content == null)
        {
            return SiteBuilderResources.Serialize(new
            {
                StoreId = store_id,
                Available = false,
                Reason = $"The theme has no custom section type '{type}'. {SiteBuilderResources.SchemasUri.Replace("{store_id}", store_id, StringComparison.Ordinal)} lists its types, marked custom; create_section_type creates one.",
            });
        }

        var status = await themeOperationService.GetStatusAsync(new SiteBuilderOperationRequest { StoreId = store_id }, context.User, cancellationToken);
        SiteBuilderChanges.EnsureSucceeded(status, $"The theme of store '{store_id}'", $"Your builder:read permission does not cover store '{store_id}'.");
        var pathInTheme = ThemeStorage.GetCustomSectionFile(type);
        var content = ParseObject(file.Content);

        return SiteBuilderResources.Serialize(new
        {
            StoreId = store_id,
            Available = true,
            Type = type,
            Path = path,
            VersionToken = file.ETag,
            Published = !status.Value.NewDrafts.Contains(pathInTheme, StringComparer.OrdinalIgnoreCase),
            HasDraft = status.Value.Drafts.Contains(pathInTheme, StringComparer.OrdinalIgnoreCase),
            Name = content?[ThemeCustomSections.NameKey]?.DeepClone(),
            Schema = content?[ThemeCustomSections.SchemaKey]?.DeepClone(),
            Template = content?[ThemeCustomSections.TemplateKey]?.DeepClone(),
            Css = content?[ThemeCustomSections.CssKey]?.DeepClone(),
            Content = content == null ? file.Content : null,
        });
    }

    private static JsonObject ParseObject(string content)
    {
        try
        {
            return JsonNode.Parse(content) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<JsonObject> ReadCatalogAsync(ClaimsPrincipal user, string storeId, CancellationToken cancellationToken)
    {
        await SiteBuilderResources.EnsureStoreAsync(authorizationService, user, storeId);

        return await ThemeFiles.GetCatalogAsync(schemaCatalogService, storeId, cancellationToken);
    }
}
