using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using VirtoCommerce.ContentModule.Core;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;

namespace VirtoCommerce.SiteBuilder.MCP;

// The theme files the design tools read and save, through the designer's own read and save, so a draft written here
// passes the checks, sanitizing and history of a draft the designer writes. Paths carry the theme folder, as the
// designer names them ("default/templates/product.json").
public static class ThemeFiles
{
    public const string CatalogHashKey = "hash";
    public const string PaletteTokensKey = "paletteTokens";

    private const string MainSectionMarker = "main section";

    public static async Task<string> GetPathAsync(IThemeStorageFactory themeStorageFactory, string storeId, string fileInTheme) =>
        $"{await themeStorageFactory.GetThemeNameAsync(storeId)}/{fileInTheme}";

    public static string GetTemplateFile(string templateKey) => ThemeStorage.GetTemplateFile(templateKey);

    // The schema catalog the store's storefront sent last: theme files are saved and published against it, as the
    // designer saves against the catalog its preview sent. Null when the storefront has sent none.
    public static async Task<JsonObject> GetCatalogAsync(ISchemaCatalogService schemaCatalogService, string storeId, CancellationToken cancellationToken)
    {
        var catalog = await schemaCatalogService.GetAsync(storeId, hash: null, cancellationToken);
        if (string.IsNullOrEmpty(catalog))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(catalog) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<string> RequireCatalogHashAsync(ISchemaCatalogService schemaCatalogService, string storeId, CancellationToken cancellationToken)
    {
        var catalog = await GetCatalogAsync(schemaCatalogService, storeId, cancellationToken);

        return GetCatalogHash(catalog) ?? throw NoCatalog(storeId);
    }

    public static string GetCatalogHash(JsonObject catalog) => catalog?[CatalogHashKey] is JsonValue value && value.TryGetValue<string>(out var hash) ? hash : null;

    public static McpException NoCatalog(string storeId) =>
        new($"The storefront of store '{storeId}' has not sent its schema catalog yet, so theme files cannot be checked and nothing was " +
            "saved. Open the store once in the designer (its preview sends the catalog), then repeat the call.");

    // The palette tokens the storefront announced with its catalog, light and dark.
    public static ThemePaletteTokens GetPaletteTokens(JsonObject catalog)
    {
        var tokens = new ThemePaletteTokens();
        if (catalog?[PaletteTokensKey] is JsonObject announced)
        {
            tokens.Light = GetStrings(announced[Palette.Light]);
            tokens.Dark = GetStrings(announced[Palette.Dark]);
        }

        return tokens;
    }

    // The file as the designer edits it: its draft, else what is live; Content is null for a file that does not exist.
    public static async Task<ThemeFileContent> ReadAsync(IDesignerOperationService designerOperationService, ClaimsPrincipal user, string storeId,
        string path, CancellationToken cancellationToken)
    {
        var result = await designerOperationService.ReadThemeFileAsync(new DesignerThemeFileRequest { StoreId = storeId, Path = path }, user, cancellationToken);
        switch (result.Status)
        {
            case SiteBuilderOperationStatus.Succeeded:
                return result.Value;
            case SiteBuilderOperationStatus.Forbidden:
                throw new McpForbiddenException($"You may not read the theme of store '{storeId}': it needs builder:read there.");
            default:
                throw new McpException($"'{path}' could not be read: {result.Error ?? "it is not a theme file of the store"}.");
        }
    }

    // The live file, which the published theme holds; null when the theme has none.
    public static async Task<string> ReadLiveAsync(ISiteBuilderContentQueryService contentQueryService, ClaimsPrincipal user, string storeId,
        string path, CancellationToken cancellationToken)
    {
        var result = await contentQueryService.ReadFileAsync(new SiteBuilderFileReadRequest
        {
            StoreId = storeId,
            ContentType = ContentConstants.ContentTypes.Themes,
            Path = path,
            Draft = false,
        }, user, cancellationToken);

        switch (result.Status)
        {
            case SiteBuilderOperationStatus.Succeeded:
                if (result.Value.Stream == null)
                {
                    return result.Value.Text;
                }

                await using (var stream = result.Value.Stream)
                {
                    using var reader = new StreamReader(stream);
                    return await reader.ReadToEndAsync(cancellationToken);
                }
            case SiteBuilderOperationStatus.NotFound:
                return null;
            case SiteBuilderOperationStatus.Forbidden:
                throw new McpForbiddenException($"You may not read the theme of store '{storeId}': it needs builder:read there.");
            default:
                throw new McpException($"'{path}' could not be read: {result.Error}.");
        }
    }

    // A dry run checks and sanitizes the files as a save does and writes nothing.
    public static Task<SiteBuilderOperationResult<DesignerSaveResult>> SaveAsync(IDesignerOperationService designerOperationService, ClaimsPrincipal user,
        string storeId, string catalogHash, IList<DesignerSaveFile> files, bool dryRun, CancellationToken cancellationToken) =>
        designerOperationService.SaveAsync(new DesignerSaveOperationRequest
        {
            StoreId = storeId,
            CatalogHash = catalogHash,
            Changes = new DesignerSaveRequest { ThemeFiles = files },
            SaveId = Guid.NewGuid().ToString("N"),
            DryRun = dryRun,
        }, user, cancellationToken);

    // A refused save as a sentence the user can act on. The permission of a file is checked by the save: templates and
    // rules need builder:templates, the other theme files builder:theme.
    public static void EnsureSaved(SiteBuilderOperationResult result, string subject, string permission, string storeId, string readAgain)
    {
        switch (result.Status)
        {
            case SiteBuilderOperationStatus.Succeeded:
                return;
            case SiteBuilderOperationStatus.Forbidden:
                throw new McpForbiddenException($"You may not change {subject}: it needs builder:update and {permission}, and builder:read in store '{storeId}'.");
            case SiteBuilderOperationStatus.Conflict:
            case SiteBuilderOperationStatus.VersionRequired:
                throw new McpException($"{subject} changed since you read it, so nothing was saved. {readAgain}");
            case SiteBuilderOperationStatus.NotFound:
                throw new McpException($"{subject} was not found.");
            default:
                var errors = result.Errors ?? (result.Error == null ? [] : [result.Error]);
                if (errors.FirstOrDefault(x => x.Contains(MainSectionMarker, StringComparison.Ordinal)) is { } mainSection)
                {
                    throw new McpException(DescribeMainSectionRefusal(subject, mainSection));
                }

                var details = errors.Count > 1 ? $" {string.Join("; ", errors)}" : string.Empty;
                throw new McpException($"{subject} was not saved: {result.Error}{details}");
        }
    }

    // The section that shows what the template's pages are about may not go: removed, or hidden where it was shown.
    private static string DescribeMainSectionRefusal(string subject, string error)
    {
        var section = System.Text.RegularExpressions.Regex.Match(error, "main section '([^']+)'", System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1)).Groups[1].Value;
        var what = section switch
        {
            "product-info" => "the product itself",
            "category-listing" => "the category's product listing",
            _ => "the page's own content",
        };
        var verb = error.EndsWith("hidden", StringComparison.Ordinal) ? "hidden" : "removed";

        return $"{subject} was not saved: its main section '{section}' cannot be {verb}, because every page the template lays out would " +
            $"lose {what}. Nothing was saved. Keep that section (you may move it or change its settings) and change the others.";
    }

    private static List<string> GetStrings(JsonNode node) =>
        (node as JsonArray)?.Select(x => x is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).Where(x => x != null).ToList() ?? [];
}
