using System.ComponentModel;
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
using VirtoCommerce.SiteBuilder.MCP.Extensions;
using VirtoCommerce.SiteBuilder.MCP.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class ThemePublishingTools(
    IAuthorizationService authorizationService,
    IThemeOperationService themeOperationService,
    ISchemaCatalogService schemaCatalogService)
{
    public const string ExpectedVersionsArgument = "expected_versions";

    [McpServerTool(Name = "publish_theme", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Publish)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Publish the theme of a store: every draft of its templates, template rules, header, footer, brand and palette " +
        "goes live at once, whoever saved it, and every page then shows the new look. First call it with dryRun: it lists the " +
        "drafts with who last changed each and expected_versions, or, when the theme cannot be published (such as with a palette " +
        "below WCAG 2.2 AA), an error naming why. Then call it without dryRun and with those expected_versions: the publication " +
        "waits for a second person's approval and publishes exactly the drafts that were reviewed; if a draft changes in between, " +
        "it is refused and nothing is published. To publish a single page use publish_page.")]
    public async Task<ThemePublication> PublishTheme(
        RequestContext<CallToolRequestParams> context,
        [Description("Store id.")] string store_id,
        [Description("The expected_versions of the dry run, by file path, unchanged. Required to publish.")] Dictionary<string, string> expected_versions = null,
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

        var change = new ThemePublication
        {
            Action = "publish_theme",
            ObjectType = "theme",
            ObjectId = store_id,
            ObjectName = $"The theme of store '{store_id}'",
            StoreId = store_id,
            DryRun = true,
        };

        foreach (var draft in check.Preview?.Objects ?? [])
        {
            change.AddChange(draft.ObjectId, "draft", "published");
            change.ExpectedVersions[draft.ObjectId] = draft.ETag;
            change.Drafts.Add(new ThemeDraftRow { Path = draft.ObjectId, ChangedBy = draft.ChangedBy?.Name ?? draft.ChangedBy?.Login, ChangedDate = draft.ChangedDate });
        }

        foreach (var warning in check.Preview?.Warnings ?? [])
        {
            change.AddWarning(warning);
        }

        if (change.Drafts.Count == 0)
        {
            change.AddWarning("The theme has no drafts: the publication puts out the live files again, and nothing customers see changes.");
        }

        // Checked on a dry run too: the approval repeats it, so moved drafts stay pending for the approver to reject.
        if (expected_versions != null && !SameVersions(expected_versions, change.ExpectedVersions))
        {
            throw new McpException("The theme's drafts changed since the dry run that gave expected_versions, so nothing was published. Call " +
                "publish_theme with dryRun again, review the drafts, and send the new expected_versions.");
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

    // A call without the versions is a dry run or is refused by the tool, so it publishes nothing and needs no approval.
    public static bool NeedsApproval(IReadOnlyDictionary<string, JsonElement> arguments) =>
        arguments != null &&
        arguments.TryGetValue(ExpectedVersionsArgument, out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

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
}
