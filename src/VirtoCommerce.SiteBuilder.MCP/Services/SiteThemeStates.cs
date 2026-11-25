using System.Security.Claims;
using ModelContextProtocol;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.MCP.Tools;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

public class SiteThemeStates(
    ISiteBuilderContentQueryService contentQueryService,
    IDesignerOperationService designerOperationService,
    IThemeOperationService themeOperationService,
    IThemeStorageFactory themeStorageFactory)
{
    private static readonly (string Part, string File)[] Parts =
    [
        (ThemeDraftTools.HeaderPart, ThemeStorage.HeaderGroupFile),
        (ThemeDraftTools.FooterPart, ThemeStorage.FooterGroupFile),
        (ThemeDraftTools.BrandPart, ThemeStorage.BrandFile),
        (ThemeDraftTools.PalettePart, ThemeStorage.PaletteFile),
        ("palette_dark", ThemeStorage.DarkPaletteFile),
    ];

    public async Task<ThemeOverview> ReadAsync(ClaimsPrincipal user, string storeId, bool full, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadThemeAsync(user, storeId, full, cancellationToken);
        }
        catch (McpException ex) when (ex is not McpForbiddenException)
        {
            return new ThemeOverview { Available = false, Note = $"Not available: {ex.Message}" };
        }
    }

    private async Task<ThemeOverview> ReadThemeAsync(ClaimsPrincipal user, string storeId, bool full, CancellationToken cancellationToken)
    {
        var status = await themeOperationService.GetStatusAsync(new SiteBuilderOperationRequest { StoreId = storeId }, user, cancellationToken);
        if (!status.Succeeded || status.Value == null)
        {
            return new ThemeOverview { Available = false, Note = $"Not available: the theme's state could not be read ({status.Error ?? status.Status.ToString()})." };
        }

        var templates = await ThemeTemplates.ListAsync(contentQueryService, user, storeId, cancellationToken);
        var overview = new ThemeOverview
        {
            Available = true,
            Publication = status.Value.Publication,
            DraftFiles = status.Value.Drafts.Count,
            Templates = templates.Count,
            TemplatesWithDrafts = templates.Where(x => x.HasDraft).Select(x => x.Key).ToList(),
            UnpublishedTemplates = templates.Where(x => !x.Published).Select(x => x.Key).ToList(),
        };

        if (status.Value.Publication == null)
        {
            overview.Notes.Add("The theme was never published: the storefront shows its own defaults until publish_theme runs.");
        }

        if (full)
        {
            overview.Rules = await ReadRulesAsync(user, storeId, status.Value, cancellationToken);
            overview.Parts = await ReadPartsAsync(user, storeId, status.Value, cancellationToken);
        }

        if (overview.DraftFiles > 0)
        {
            overview.Notes.Add("get_publish_readiness with theme true lists every draft with who changed it.");
        }

        return overview;
    }

    private async Task<RulesOverview> ReadRulesAsync(ClaimsPrincipal user, string storeId, ThemeStatus status, CancellationToken cancellationToken)
    {
        var (rules, _) = await ThemeTemplates.ReadRulesAsync(designerOperationService, themeStorageFactory, user, storeId, cancellationToken);
        var all = TemplateRulesDocument.PageTypes.SelectMany(rules.RulesOf).ToList();

        return new RulesOverview
        {
            Rules = all.Count,
            Disabled = all.Count(TemplateRulesDocument.IsDisabled),
            OwnRulesFor = TemplateRulesDocument.PageTypes.Where(rules.HasOwnRules).ToList(),
            StorefrontDefaultsFor = TemplateRulesDocument.PageTypes.Where(x => !rules.HasOwnRules(x)).ToList(),
            HasDraft = IsDraft(status, ThemeStorage.TemplateRulesFile),
        };
    }

    private async Task<IList<ThemePartState>> ReadPartsAsync(ClaimsPrincipal user, string storeId, ThemeStatus status, CancellationToken cancellationToken)
    {
        var parts = new List<ThemePartState>();
        foreach (var (part, file) in Parts)
        {
            parts.Add(new ThemePartState { Part = part, File = file, State = await GetPartStateAsync(user, storeId, status, file, cancellationToken) });
        }

        return parts;
    }

    private async Task<string> GetPartStateAsync(ClaimsPrincipal user, string storeId, ThemeStatus status, string file, CancellationToken cancellationToken)
    {
        if (IsDraft(status, file))
        {
            return status.NewDrafts.Contains(file, StringComparer.OrdinalIgnoreCase) ? "draft_never_published" : "draft";
        }

        var path = await ThemeFiles.GetPathAsync(themeStorageFactory, storeId, file);

        return await ThemeFiles.ReadLiveAsync(contentQueryService, user, storeId, path, cancellationToken) == null ? "storefront_default" : "published";
    }

    private static bool IsDraft(ThemeStatus status, string file) => status.Drafts.Contains(file, StringComparer.OrdinalIgnoreCase);
}
