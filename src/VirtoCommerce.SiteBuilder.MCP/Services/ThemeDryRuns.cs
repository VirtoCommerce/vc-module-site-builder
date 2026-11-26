using System.Security.Claims;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.MCP.Services.PublishChecks;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

public class ThemeDryRuns(
    IThemeOperationService themeOperationService,
    ISchemaCatalogService schemaCatalogService,
    RemovedSectionTypes removedSectionTypes)
{
    private const string ContrastMarker = "contrast";

    public async Task CheckAsync(ClaimsPrincipal user, string storeId, PublishReadiness readiness, CancellationToken cancellationToken)
    {
        var catalogHash = await schemaCatalogService.GetCurrentHashAsync(storeId, cancellationToken);
        if (catalogHash == null)
        {
            readiness.Findings.Add(Blocker("schema-catalog-missing", "The storefront has not sent its schema catalog, so the theme cannot be checked or published.",
                $"Store '{storeId}' has no current schema catalog.", "Open the store once in the designer: its preview sends the catalog. Then check again.", "catalog"));
            return;
        }

        var check = await themeOperationService.PublishAsync(new ThemePublishOperationRequest { StoreId = storeId, CatalogHash = catalogHash, DryRun = true },
            user, cancellationToken);
        EnsureAllowed(check, $"Your builder:read permission does not cover store '{storeId}'.");

        if (!check.Succeeded)
        {
            AddRefusals(check, readiness);
            return;
        }

        AddDryRun(check, storeId, await removedSectionTypes.FindAsync(user, storeId, cancellationToken), readiness);
    }

    private static void AddDryRun(SiteBuilderOperationResult check, string storeId, IList<RemovedSectionType> removed, PublishReadiness readiness)
    {
        foreach (var draft in check.Preview?.Objects ?? [])
        {
            readiness.Parts.Add(PublishPart.FromPreview(draft));
        }

        foreach (var type in removed)
        {
            readiness.Parts.Add(new PublishPart
            {
                ObjectType = ChangePreview.ThemeFile,
                ObjectId = type.Path,
                Changes = [new PartChange("status", RemovedSectionType.PublishedStatus, RemovedSectionType.RemovedStatus)],
            });
            readiness.Findings.Add(Warning("section-type-removed", type.Warning, $"Store '{storeId}': {type.Path} is live, but the theme no longer holds it.",
                "To keep it, create it again with create_section_type before publishing; otherwise remove its sections from the pages that place it.", "theme"));
        }

        foreach (var warning in check.Preview?.Warnings ?? [])
        {
            readiness.Findings.Add(Warning("theme-warning", warning, "The dry run of the theme's publication.",
                "Review it before publishing; it does not stop the publication.", "theme"));
        }

        if (readiness.Parts.Count == 0)
        {
            readiness.Findings.Add(Info("nothing-to-publish", "The theme has no drafts: a publication would put out the live files again.",
                $"Store '{storeId}' has no theme drafts.", "Nothing to do; edit_theme_draft, edit_template, edit_template_rules, create_section_type and " +
                "change_section_type make drafts.", "theme"));
        }
    }

    private static void AddRefusals(SiteBuilderOperationResult check, PublishReadiness readiness)
    {
        var errors = check.Errors?.Count > 0 ? check.Errors : [check.Error ?? check.Status.ToString()];
        foreach (var error in errors)
        {
            readiness.Findings.Add(Refusal(error));
        }
    }

    private static McpFinding Refusal(string error)
    {
        if (error.Contains(ContrastMarker, StringComparison.OrdinalIgnoreCase))
        {
            return Blocker("palette-contrast", "A color pair of the palette is below the WCAG 2.2 AA contrast, so the theme cannot be published.",
                error, "Fix the palette with edit_theme_draft (part palette): regenerate it or change the failing colors.", "contrast");
        }

        return Blocker("theme-refused", "The theme's publication would be refused.", error, "Fix the draft it names, then check again.", "validation");
    }
}
