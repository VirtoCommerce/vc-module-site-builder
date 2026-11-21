using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.CustomerModule.Core.Model;
using VirtoCommerce.CustomerModule.Core.Model.Search;
using VirtoCommerce.CustomerModule.Core.Services;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.Platform.Core;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.MCP.Extensions;
using VirtoCommerce.SiteBuilder.MCP.Services;
using VirtoCommerce.StoreModule.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;
using CustomerPermissions = VirtoCommerce.CustomerModule.Core.ModuleConstants.Security.Permissions;
using CustomerSettings = VirtoCommerce.CustomerModule.Core.ModuleConstants.Settings.General;

namespace VirtoCommerce.SiteBuilder.MCP.Resources;

[McpServerResourceType]
[McpScope(McpScopes.Admin)]
public class SiteBuilderResources(
    IAuthorizationService authorizationService,
    ISchemaCatalogService schemaCatalogService,
    IStoreService storeService,
    ISettingsManager settingsManager,
    IMemberSearchService memberSearchService,
    ISiteBuilderPublishStatusQueryService publishStatusQueryService,
    IMcpPageSizeResolver pageSizeResolver,
    IThemeStorageFactory themeStorageFactory,
    IDesignerOperationService designerOperationService,
    IThemeOperationService themeOperationService,
    ICustomSectionService customSectionService,
    ICustomSectionValidator customSectionValidator,
    ILogger<SiteBuilderResources> logger)
{
    public const string SchemasUri = "site-builder://stores/{store_id}/schemas";

    private const string JsonMimeType = "application/json";
    private const string NameKey = "name";

    private static readonly string[] SchemaKindsListed = [SchemaKinds.Sections, SchemaKinds.Blocks, SchemaKinds.Objects, SchemaKinds.Templates];

    [McpServerResource(UriTemplate = SchemasUri, Name = "section_schemas", MimeType = JsonMimeType)]
    [McpPermission(Read)]
    [Description("The schemas the store's storefront sent: the section, block, object and template types with their names, " +
        "and the custom section types of the store's theme among the sections, marked custom. Read one schema in full at " +
        "site-builder://stores/{store_id}/schemas/{kind}/{key}. Until the storefront has sent its catalog (the designer's preview " +
        "sends it), the answer says the schemas are not available.")]
    public async Task<string> SectionSchemas(RequestContext<ReadResourceRequestParams> context, string store_id, CancellationToken cancellationToken)
    {
        var catalog = await ReadThemeCatalogAsync(context.User, store_id, cancellationToken);
        if (catalog == null)
        {
            return Serialize(NoCatalog(store_id));
        }

        var kinds = SchemaKindsListed.ToDictionary(x => x, x => ListSchemas(catalog[x] as JsonObject));

        return Serialize(new
        {
            StoreId = store_id,
            Available = true,
            CatalogHash = ThemeFiles.GetCatalogHash(catalog),
            Kinds = kinds,
            Note = $"Read one schema with its settings at site-builder://stores/{store_id}/schemas/{{kind}}/{{key}}, for example .../schemas/sections/hero-banner. " +
                $"A custom section type is the theme's own: read it whole at site-builder://stores/{store_id}/custom-sections/{{type}} and change it with change_section_type; " +
                $"create one with create_section_type after reading site-builder://stores/{store_id}/section-kit.",
        });
    }

    [McpServerResource(UriTemplate = "site-builder://stores/{store_id}/schemas/{kind}/{key}", Name = "section_schema", MimeType = JsonMimeType)]
    [McpPermission(Read)]
    [Description("One schema the store's storefront sent, or of a custom section type of the store's theme, with its settings and blocks: " +
        "kind is sections, blocks, objects or templates; key is the type.")]
    public async Task<string> SectionSchema(RequestContext<ReadResourceRequestParams> context, string store_id, string kind, string key, CancellationToken cancellationToken)
    {
        var catalog = await ReadThemeCatalogAsync(context.User, store_id, cancellationToken);
        if (catalog == null)
        {
            return Serialize(NoCatalog(store_id));
        }

        if ((catalog[kind ?? string.Empty] as JsonObject)?[key ?? string.Empty] is not JsonObject schema)
        {
            return Serialize(new
            {
                StoreId = store_id,
                Available = false,
                Reason = $"The storefront's catalog has no {kind} schema '{key}'. {SchemasUri.Replace("{store_id}", store_id, StringComparison.Ordinal)} lists them.",
            });
        }

        return Serialize(new { StoreId = store_id, Available = true, Kind = kind, Key = key, Schema = schema.DeepClone() });
    }

    [McpServerResource(UriTemplate = "site-builder://stores/{store_id}/section-kit", Name = "section_kit", MimeType = JsonMimeType)]
    [McpPermission(Read)]
    [Description("Everything to write a custom section type with create_section_type or change_section_type: the elements and attributes a template may hold, " +
        "the bindings, the setting types, the setting variables, the CSS rules, the store's palette tokens, the kit tags and behaviors " +
        "the storefront offers, the storefront's own guide and design tokens, the design rules, the size limits and a complete example. " +
        "Read it before writing a type.")]
    public async Task<string> ReadSectionKit(RequestContext<ReadResourceRequestParams> context, string store_id, CancellationToken cancellationToken)
    {
        var catalog = await ReadCatalogAsync(context.User, store_id, cancellationToken);
        var palette = AbstractTypeFactory<ThemePalette>.TryCreateInstance();
        palette.Light = await ReadPaletteColorsAsync(context.User, store_id, ThemeStorage.PaletteFile, cancellationToken);
        palette.Dark = await ReadPaletteColorsAsync(context.User, store_id, ThemeStorage.DarkPaletteFile, cancellationToken);

        return Serialize(SectionKit.Describe(store_id, catalog, customSectionValidator.GetRules(), palette));
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

    [McpServerResource(UriTemplate = "site-builder://stores/{store_id}/custom-sections/{type}", Name = "custom_section", MimeType = JsonMimeType)]
    [McpPermission(Read)]
    [Description("A custom section type of the store's theme as change_section_type takes it: name, schema, template and css, as its draft " +
        "holds them, else as they are live, with its version_token and whether it is published. A type only in draft shows nothing " +
        "on the storefront until the theme is published.")]
    public async Task<string> ReadCustomSection(RequestContext<ReadResourceRequestParams> context, string store_id, string type, CancellationToken cancellationToken)
    {
        await EnsureStoreAsync(context.User, store_id);
        if (string.IsNullOrEmpty(type) || !ThemeStorage.IsCustomSectionType(type))
        {
            return Serialize(new
            {
                StoreId = store_id,
                Available = false,
                Reason = $"'{type}' is not a custom section type, which is 'custom-' and lowercase words joined by '-'.",
            });
        }

        var path = await ThemeFiles.GetPathAsync(themeStorageFactory, store_id, ThemeStorage.GetCustomSectionFile(type));
        var file = await ThemeFiles.ReadAsync(designerOperationService, context.User, store_id, path, cancellationToken);
        if (file.Content == null)
        {
            return Serialize(new
            {
                StoreId = store_id,
                Available = false,
                Reason = $"The theme has no custom section type '{type}'. {SchemasUri.Replace("{store_id}", store_id, StringComparison.Ordinal)} lists its types, marked custom; create_section_type creates one.",
            });
        }

        var status = await themeOperationService.GetStatusAsync(new SiteBuilderOperationRequest { StoreId = store_id }, context.User, cancellationToken);
        SiteBuilderChanges.EnsureSucceeded(status, $"The theme of store '{store_id}'", $"Your builder:read permission does not cover store '{store_id}'.");
        var pathInTheme = ThemeStorage.GetCustomSectionFile(type);
        var content = ParseObject(file.Content);

        return Serialize(new
        {
            StoreId = store_id,
            Available = true,
            Type = type,
            Path = path,
            VersionToken = file.ETag,
            Published = !status.Value.NewDrafts.Contains(pathInTheme, StringComparer.OrdinalIgnoreCase),
            HasDraft = status.Value.Drafts.Contains(pathInTheme, StringComparer.OrdinalIgnoreCase),
            Name = content?[NameKey]?.DeepClone(),
            Schema = content?["schema"]?.DeepClone(),
            Template = content?["template"]?.DeepClone(),
            Css = content?["css"]?.DeepClone(),
            Content = content == null ? file.Content : null,
        });
    }

    [McpServerResource(UriTemplate = "site-builder://template-rule-conditions", Name = "template_rule_conditions", MimeType = JsonMimeType)]
    [McpPermission(Read)]
    [Description("The conditions a template rule can use, with the page types each applies to and the shape of its value: what " +
        "edit_template_rules accepts and get_preview evaluates.")]
    public static string TemplateRuleConditionList()
    {
        return Serialize(new
        {
            Conditions = TemplateRuleConditions.All.Select(x => new { x.Name, x.PageTypes, Value = x.Shape }).ToList(),
            Notes = new[]
            {
                "A condition named with a prefix, such as loyalty.tier, belongs to a storefront plugin: it is kept as written and evaluated only by the storefront.",
                "inCategory, productType and property read the catalog, which the server does not: get_preview marks rules with them as may_match.",
                "Rules are tried in order and the first enabled rule whose conditions all hold wins; a rule without conditions matches every page of its type.",
            },
        });
    }

    [McpServerResource(UriTemplate = "site-builder://stores/{store_id}/settings", Name = "store_settings", MimeType = JsonMimeType)]
    [McpPermission(Read, PlatformConstants.Security.Permissions.SettingQuery)]
    [Description("A store's languages (the default one first), its time zone, and how pages stored as files go live (git: a pull " +
        "request; blob: at once). Page dates are stored in UTC.")]
    public async Task<string> StoreSettings(RequestContext<ReadResourceRequestParams> context, string store_id, CancellationToken cancellationToken)
    {
        await EnsureStoreAsync(context.User, store_id);
        var store = await storeService.GetNoCloneAsync(store_id)
            ?? throw new McpException($"Store '{store_id}' was not found.");

        var (flow, flowNote) = await ReadFlowAsync(context.User, store_id, cancellationToken);
        var languages = (store.Languages ?? []).OrderBy(x => x == store.DefaultLanguage ? 0 : 1).ToList();

        return Serialize(new
        {
            StoreId = store.Id,
            StoreName = store.Name,
            DefaultLanguage = store.DefaultLanguage,
            Languages = languages,
            TimeZone = store.TimeZone,
            PublicationFlow = flow,
            PublicationFlowNote = flowNote,
        });
    }

    private async Task<(string Flow, string Note)> ReadFlowAsync(ClaimsPrincipal user, string storeId, CancellationToken cancellationToken)
    {
        SiteBuilderOperationResult<PagePublishStatus> result;
        try
        {
            result = await publishStatusQueryService.GetFilePageStatusAsync(new SiteBuilderFilePageQueryRequest { StoreId = storeId }, user, cancellationToken);
        }
        catch (GitContentException ex)
        {
            return (null, $"Not available. {GitContentFailures.Describe(logger, ex)}");
        }

        if (result?.Succeeded == true && result.Value?.Flow != null)
        {
            return (result.Value.Flow, null);
        }

        return (null, $"Not available: {result?.Error ?? "the publication flow could not be read"}.");
    }

    [McpServerResource(UriTemplate = "site-builder://user-groups{?skip,take}", Name = "user_groups", MimeType = JsonMimeType)]
    [McpPermission(Read, PlatformConstants.Security.Permissions.SettingQuery)]
    [Description("The user groups a page's audience can name (change_page_settings, user_groups), as the platform's customer " +
        "settings list them. Paged: skip and take, next_skip for the next page.")]
    public async Task<string> UserGroups(int? skip = null, int? take = null)
    {
        var setting = await settingsManager.GetObjectSettingAsync(CustomerSettings.MemberGroups.Name);
        var groups = (setting?.AllowedValues ?? []).Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)).Order(StringComparer.OrdinalIgnoreCase);

        return Serialize(McpPage<string>.FromList(groups, await pageSizeResolver.GetPagingAsync(skip, take)));
    }

    [McpServerResource(UriTemplate = "site-builder://organizations{?keyword,skip,take}", Name = "organizations", MimeType = JsonMimeType)]
    [McpPermission(Read)]
    // Unscoped only: a role limited to some organizations would see the others' names.
    [Authorize(CustomerPermissions.Read)]
    [Description("Organizations a page's audience can name (change_page_settings, organization_id): id and name, filtered by " +
        "keyword. Needs customer:read for every organization. Paged: skip and take, next_skip for the next page.")]
    public async Task<string> Organizations(string keyword = null, int? skip = null, int? take = null)
    {
        var paging = await pageSizeResolver.GetPagingAsync(skip, take);
        var result = await memberSearchService.SearchMembersAsync(new MembersSearchCriteria
        {
            MemberType = nameof(Organization),
            DeepSearch = true,
            Keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim(),
            Skip = paging.Skip,
            Take = paging.Take,
        });

        return Serialize(McpPage<OrganizationRow>.Create(result.Results.Select(x => new OrganizationRow(x.Id, x.Name ?? x.Id)), result.TotalCount, paging));
    }

    private async Task<JsonObject> ReadCatalogAsync(ClaimsPrincipal user, string storeId, CancellationToken cancellationToken)
    {
        await EnsureStoreAsync(user, storeId);

        return await ThemeFiles.GetCatalogAsync(schemaCatalogService, storeId, cancellationToken);
    }

    // With the theme's custom section types, which the storefront's preview may not have seen yet.
    private async Task<JsonObject> ReadThemeCatalogAsync(ClaimsPrincipal user, string storeId, CancellationToken cancellationToken)
    {
        var catalog = await ReadCatalogAsync(user, storeId, cancellationToken);

        if (catalog == null)
        {
            return null;
        }

        var result = await customSectionService.GetThemeCatalogAsync(storeId, ThemeJson.ParseObject(catalog.ToJsonString()), files: null, cancellationToken);

        return JsonNode.Parse(result.ToString(Newtonsoft.Json.Formatting.None)) as JsonObject;
    }

    private Task EnsureStoreAsync(ClaimsPrincipal user, string storeId)
    {
        if (string.IsNullOrWhiteSpace(storeId))
        {
            throw new McpException("Name the store in the address: site-builder://stores/{store_id}/....");
        }

        return authorizationService.EnsureStoreAllowedAsync(user, storeId);
    }

    private static object NoCatalog(string storeId) => new
    {
        StoreId = storeId,
        Available = false,
        Reason = "Not available: the storefront of the store has not sent its schema catalog yet. Open the store once in the designer; its preview sends it.",
    };

    private static List<SchemaRow> ListSchemas(JsonObject schemas) =>
        schemas?.Select(x => new SchemaRow(x.Key, GetName((x.Value as JsonObject)?[NameKey]) ?? x.Key, IsCustom(x.Value as JsonObject) ? true : null))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList() ?? [];

    // A custom section type may name itself per language.
    private static string GetName(JsonNode name) => name switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonObject translations => translations.Select(x => GetName(x.Value)).FirstOrDefault(x => x != null),
        _ => null,
    };

    private static bool IsCustom(JsonObject schema) => schema?[ThemeCustomSections.CustomKey] is JsonValue custom && custom.TryGetValue<bool>(out var value) && value;

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

    private static string Serialize(object value) => JsonSerializer.Serialize(value, McpSerialization.Options);
}

public sealed record SchemaRow(string Key, string Name, bool? Custom = null);

public sealed record OrganizationRow(string Id, string Name);
