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
using VirtoCommerce.StoreModule.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;
using CustomerPermissions = VirtoCommerce.CustomerModule.Core.ModuleConstants.Security.Permissions;
using CustomerSettings = VirtoCommerce.CustomerModule.Core.ModuleConstants.Settings.General;

namespace VirtoCommerce.SiteBuilder.MCP.Resources;

// Reference data the agent reads before it writes: the schemas the storefront sent, the conditions template rules take,
// a store's languages and publication flow, and the user groups and organizations a page's audience names. Each is
// read with the permissions the designer's own lists need, and in the stores the user reads.
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
    ILogger<SiteBuilderResources> logger)
{
    public const string SchemasUri = "site-builder://stores/{store_id}/schemas";

    private const string JsonMimeType = "application/json";
    private const string NameKey = "name";

    private static readonly string[] SchemaKindsListed = [SchemaKinds.Sections, SchemaKinds.Blocks, SchemaKinds.Objects, SchemaKinds.Templates];

    [McpServerResource(UriTemplate = SchemasUri, Name = "section_schemas", MimeType = JsonMimeType)]
    [McpPermission(Read)]
    [Description("The schemas the store's storefront sent: the section, block, object and template types with their names. " +
        "Read one schema in full at site-builder://stores/{store_id}/schemas/{kind}/{key}. Until the storefront has sent its " +
        "catalog (the designer's preview sends it), the answer says the schemas are not available.")]
    public async Task<string> SectionSchemas(RequestContext<ReadResourceRequestParams> context, string store_id, CancellationToken cancellationToken)
    {
        var catalog = await ReadCatalogAsync(context.User, store_id, cancellationToken);
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
            Note = $"Read one schema with its settings at site-builder://stores/{store_id}/schemas/{{kind}}/{{key}}, for example .../schemas/sections/hero-banner.",
        });
    }

    [McpServerResource(UriTemplate = "site-builder://stores/{store_id}/schemas/{kind}/{key}", Name = "section_schema", MimeType = JsonMimeType)]
    [McpPermission(Read)]
    [Description("One schema the store's storefront sent, with its settings and blocks: kind is sections, blocks, objects or templates; key is the type.")]
    public async Task<string> SectionSchema(RequestContext<ReadResourceRequestParams> context, string store_id, string kind, string key, CancellationToken cancellationToken)
    {
        var catalog = await ReadCatalogAsync(context.User, store_id, cancellationToken);
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

    // git or blob, or why it is not known.
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
    [McpPermission(Read, CustomerPermissions.Read)]
    [Description("Organizations a page's audience can name (change_page_settings, organization_id): id and name, filtered by " +
        "keyword. Needs customer:read for every organization. Paged: skip and take, next_skip for the next page.")]
    public async Task<string> Organizations(RequestContext<ReadResourceRequestParams> context, string keyword = null, int? skip = null, int? take = null)
    {
        // A role limited to some organizations would see the others' names here: the designer's list needs the permission in full too.
        if (context.User.GetMcpPermissionGrant(CustomerPermissions.Read)?.IsGlobal != true)
        {
            throw new McpForbiddenException($"Listing organizations needs {CustomerPermissions.Read} for every organization, as the designer's list does.");
        }

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

    private Task EnsureStoreAsync(ClaimsPrincipal user, string storeId)
    {
        if (string.IsNullOrWhiteSpace(storeId))
        {
            throw new McpException("Name the store in the address: site-builder://stores/{store_id}/....");
        }

        return authorizationService.EnsureAllowedAsync(user, new SiteBuilderPageSearchCriteria { StoreId = storeId }, storeId);
    }

    private static object NoCatalog(string storeId) => new
    {
        StoreId = storeId,
        Available = false,
        Reason = "Not available: the storefront of the store has not sent its schema catalog yet. Open the store once in the designer; its preview sends it.",
    };

    private static List<SchemaRow> ListSchemas(JsonObject schemas) =>
        schemas?.Select(x => new SchemaRow(x.Key, (x.Value as JsonObject)?[NameKey] is JsonValue name && name.TryGetValue<string>(out var text) ? text : x.Key))
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToList() ?? [];

    private static string Serialize(object value) => JsonSerializer.Serialize(value, McpSerialization.Options);
}

public sealed record SchemaRow(string Key, string Name);

public sealed record OrganizationRow(string Id, string Name);
