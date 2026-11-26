using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Models;
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
public class ThemePublishingTools(
    IAuthorizationService authorizationService,
    IThemeOperationService themeOperationService,
    ISchemaCatalogService schemaCatalogService,
    IDesignerOperationService designerOperationService,
    ISiteBuilderContentQueryService contentQueryService,
    SectionTypePages sectionTypePages,
    RemovedSectionTypes removedSectionTypes)
{
    public const string ExpectedVersionsArgument = "expected_versions";
    public const string ExpectedCatalogHashArgument = "expected_catalog_hash";

    [McpServerTool(Name = "publish_theme", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Publish)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Publish the theme of a store: every draft of its templates, template rules, header, footer, brand, palette and " +
        "custom section types goes live at once, whoever saved it, and every page then shows the new look, with the custom section " +
        "types it places as their drafts hold them. First call it with dryRun: it lists the drafts with who last changed each, for a " +
        "custom section type what changes against its live version and the pages that place it, expected_versions and catalog_hash " +
        "(the storefront catalog the theme is checked against), or, when the theme cannot be published (such as with a palette below " +
        "WCAG 2.2 AA), an error naming why. It also warns of a custom section type live now whose file is gone, which the publication " +
        "removes, naming the pages that place it. Then call it without dryRun and with those expected_versions and that catalog_hash as " +
        "expected_catalog_hash: the publication waits for a second person's approval and publishes exactly the drafts that were " +
        "reviewed, against the catalog that was reviewed; if a draft or the catalog changes in between, it is refused and nothing is " +
        "published. To publish a single page use publish_page.")]
    public async Task<ThemePublication> PublishTheme(
        RequestContext<CallToolRequestParams> context,
        [Description("Store id.")] string store_id,
        [Description("The expected_versions of the dry run, by file path, unchanged. Required to publish.")] Dictionary<string, string> expected_versions = null,
        [Description("The catalog_hash of the dry run, unchanged. Required with expected_versions.")] string expected_catalog_hash = null,
        [Description("true to list what the publication would put live, without publishing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(store_id))
        {
            throw new McpException("Pass store_id: the theme belongs to a store.");
        }

        await authorizationService.EnsureStoreAllowedAsync(context.User, store_id);
        var catalogHash = await ThemeFiles.RequireCatalogHashAsync(schemaCatalogService, store_id, cancellationToken);

        var check = await themeOperationService.PublishAsync(new ThemePublishOperationRequest { StoreId = store_id, CatalogHash = catalogHash, DryRun = true },
            context.User, cancellationToken);
        EnsurePublished(check, store_id);

        var change = await DescribeDraftsAsync(check, store_id, catalogHash, dryRun, context.User, cancellationToken);

        // Checked on a dry run too: the approval repeats it, so moved drafts or a moved catalog stay pending for the approver to reject.
        if (expected_versions != null && !SameVersions(expected_versions, change.ExpectedVersions))
        {
            throw new McpException("The theme's drafts changed since the dry run that gave expected_versions, so nothing was published. Call " +
                "publish_theme with dryRun again, review the drafts, and send the new expected_versions.");
        }

        if (expected_versions != null)
        {
            CheckCatalogHash(expected_catalog_hash, catalogHash);
        }

        if (dryRun)
        {
            return change;
        }

        if (expected_versions == null)
        {
            throw new McpException("Nothing was published: pass expected_versions, the versions of the drafts the dry run listed, so that " +
                "exactly those drafts are approved and published. Call publish_theme with dryRun first.");
        }

        var result = await themeOperationService.PublishAsync(new ThemePublishOperationRequest
        {
            StoreId = store_id,
            CatalogHash = catalogHash,
            ExpectedVersions = expected_versions,
            ReportImpact = true,
        }, context.User, cancellationToken);
        EnsurePublished(result, store_id);

        change.DryRun = false;
        change.Publication = result.Value?.Publication;

        return change;
    }

    private async Task<ThemePublication> DescribeDraftsAsync(SiteBuilderOperationResult<ThemePublicationPointer> check, string storeId,
        string catalogHash, bool dryRun, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var change = new ThemePublication
        {
            Action = "publish_theme",
            ObjectType = "theme",
            ObjectId = storeId,
            ObjectName = $"The theme of store '{storeId}'",
            StoreId = storeId,
            CatalogHash = catalogHash,
            DryRun = true,
        };

        await AddDraftsAsync(change, check.Preview, storeId, dryRun, user, cancellationToken);

        foreach (var warning in check.Preview?.Warnings ?? [])
        {
            change.AddWarning(warning);
        }

        var removed = await removedSectionTypes.FindAsync(user, storeId, cancellationToken);
        foreach (var type in removed)
        {
            change.AddChange(type.Path, RemovedSectionType.PublishedStatus, RemovedSectionType.RemovedStatus);
            change.AddWarning(type.Warning);
        }

        if (change.Drafts.Count == 0)
        {
            change.AddWarning(removed.Count > 0
                ? "The theme has no drafts: the publication puts out the live files again."
                : "The theme has no drafts: the publication puts out the live files again, and nothing customers see changes.");
        }

        return change;
    }

    private async Task AddDraftsAsync(ThemePublication change, SiteBuilderChangePreview preview, string storeId, bool dryRun, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        foreach (var draft in preview?.Objects ?? [])
        {
            change.AddChange(draft.ObjectId, "draft", "published");
            change.ExpectedVersions[draft.ObjectId] = draft.ETag;
            change.Drafts.Add(ToDraftRow(draft, dryRun ? await DescribeSectionTypeAsync(user, storeId, draft, change, cancellationToken) : null));
        }
    }

    private static ThemeDraftRow ToDraftRow(SiteBuilderObjectChange draft, SectionTypePlacements placedSections) =>
        new() { Path = draft.ObjectId, ChangedBy = draft.ChangedBy?.Name ?? draft.ChangedBy?.Login, ChangedDate = draft.ChangedDate, PlacedSections = placedSections };

    // A call without the versions is a dry run or is refused by the tool, so it publishes nothing and needs no approval.
    public static bool NeedsApproval(IReadOnlyDictionary<string, JsonElement> arguments) =>
        arguments != null &&
        arguments.TryGetValue(ExpectedVersionsArgument, out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    // The catalog decides what the publication checks and sanitizes, so it is pinned with the drafts.
    private static void CheckCatalogHash(string expected, string current)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new McpException($"Nothing was published: pass {ExpectedCatalogHashArgument}, the catalog_hash the dry run answers, so that the theme " +
                "is published against the storefront catalog that was reviewed.");
        }

        if (!string.Equals(expected.Trim(), current, StringComparison.Ordinal))
        {
            throw new McpException($"The store's schema catalog changed since the dry run that gave {ExpectedCatalogHashArgument}, so nothing was " +
                "published. Call publish_theme with dryRun again, review the drafts, and send the new expected_versions and catalog_hash.");
        }
    }

    // A custom section type changes every page that places it at once: the dry run shows how it changes and where.
    private async Task<SectionTypePlacements> DescribeSectionTypeAsync(ClaimsPrincipal user, string storeId, SiteBuilderObjectChange draft,
        ThemePublication change, CancellationToken cancellationToken)
    {
        var type = ThemeCustomSections.GetCustomSectionType(ThemeFileEdits.GetPathInTheme(draft.ObjectId));
        if (type == null || !ThemeStorage.IsCustomSectionType(type))
        {
            return null;
        }

        var file = await ThemeFiles.ReadAsync(designerOperationService, user, storeId, draft.ObjectId, cancellationToken);
        if (file.ETag != draft.ETag)
        {
            throw new McpException("The theme's drafts changed while the dry run read them, so nothing was published. Call publish_theme with dryRun again.");
        }

        var live = await ThemeFiles.ReadLiveAsync(contentQueryService, user, storeId, draft.ObjectId, cancellationToken);
        foreach (var item in ThemeFileEdits.Compare(draft.ObjectId, live, file.Content))
        {
            change.AddChange($"{draft.ObjectId} {item.Field}", item.From, item.To);
        }

        var (pages, coverage) = await sectionTypePages.FindPublishedAndDraftsAsync(user, storeId, type, cancellationToken);
        // a type that is not live yet is shown for the first time, so none of its settings is new to a section that showed it
        var settings = live == null
            ? SectionTypeSettingChanges.None
            : SectionTypeSettingChanges.Compare(SectionTypeSettingChanges.ReadSchema(live), SectionTypeSettingChanges.ReadSchema(file.Content));

        return settings.ToPlacements(pages, coverage);
    }

    private static bool SameVersions(IDictionary<string, string> expected, IDictionary<string, string> current)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in expected)
        {
            normalized[key?.TrimStart('/') ?? string.Empty] = value?.Trim();
        }

        return normalized.Count == current.Count &&
            current.All(x => normalized.TryGetValue(x.Key, out var eTag) && string.Equals(eTag, x.Value, StringComparison.Ordinal));
    }

    private static void EnsurePublished(SiteBuilderOperationResult result, string storeId)
    {
        switch (result.Status)
        {
            case SiteBuilderOperationStatus.Succeeded:
                return;
            case SiteBuilderOperationStatus.Forbidden:
                throw new McpForbiddenException($"You may not publish the theme of store '{storeId}': it needs builder:publish, and builder:read there.");
            case SiteBuilderOperationStatus.Conflict:
                throw new McpException("The theme's drafts changed since they were reviewed, so nothing was published. Call publish_theme with " +
                    "dryRun again, review the drafts, and send the new expected_versions for approval.");
            default:
                var details = result.Errors?.Count > 0 ? $": {string.Join("; ", result.Errors)}" : $": {result.Error}";
                throw new McpException($"The theme of store '{storeId}' cannot be published{details}. Nothing was published; fix the drafts " +
                    "(a palette pair below the contrast: edit_theme_draft) and dry-run again.");
        }
    }
}

public sealed class ThemePublication : McpChangePreview
{
    [JsonPropertyName("store_id")]
    public string StoreId { get; set; }

    [JsonPropertyName("expected_versions")]
    public IDictionary<string, string> ExpectedVersions { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("catalog_hash")]
    public string CatalogHash { get; set; }

    [JsonPropertyName("drafts")]
    public IList<ThemeDraftRow> Drafts { get; set; } = [];

    [JsonPropertyName("publication")]
    public int? Publication { get; set; }
}

public sealed class ThemeDraftRow
{
    [JsonPropertyName("path")]
    public string Path { get; init; }

    [JsonPropertyName("changed_by")]
    public string ChangedBy { get; init; }

    [JsonPropertyName("changed_date")]
    public DateTime? ChangedDate { get; init; }

    // a custom section type's draft only: the publication changes the published pages at once, and the page drafts that place it from then on
    [JsonPropertyName("placed_sections")]
    public SectionTypePlacements PlacedSections { get; init; }
}
