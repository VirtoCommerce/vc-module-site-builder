using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// The theme's look as the designer edits it: the header and footer (their section groups), the brand's logos and the
// palette, light and dark. Each goes in a draft that customers see with the theme's publication. A palette below the
// WCAG 2.2 AA contrast can be saved, as in the designer, but the publication refuses it, so a change shows the failing
// pairs at once.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class ThemeDraftTools(
    IAuthorizationService authorizationService,
    IDesignerOperationService designerOperationService,
    IThemeStorageFactory themeStorageFactory,
    ISchemaCatalogService schemaCatalogService,
    IThemePaletteService themePaletteService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService pageContentService,
    ISiteBuilderSharedComponentService sharedComponentService,
    ISiteBuilderSharedComponentContentService sharedComponentContentService)
{
    public const string HeaderPart = "header";
    public const string FooterPart = "footer";
    public const string BrandPart = "brand";
    public const string PalettePart = "palette";

    private const int MaxOperations = 100;

    // The brand fields and the logos of settings_data.json a theme without a brand file shows (the designer's themeLook.ts).
    private static readonly (string Field, string Argument, string SettingsKey)[] BrandFields =
    [
        ("logoUrl", "logo_url", "logo_image"),
        ("secondaryLogoUrl", "secondary_logo_url", "logo_inverted_image"),
        ("faviconUrl", "favicon_url", "favicon_image"),
    ];

    [McpServerTool(Name = "edit_theme_draft", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [Authorize(Theme)]
    [Description("Edit a part of a store's theme draft. header / footer: the sections and blocks of the site's header or footer, " +
        "with the operations of edit_page_draft. brand: the logo, the logo for dark backgrounds and the favicon. palette: " +
        "generate the light and dark palette from one or two brand colors (primary_color, secondary_color), or set colors by " +
        "their token (light_colors, dark_colors); the answer lists the color pairs below the WCAG 2.2 AA contrast, which the " +
        "theme cannot be published with. Call it with the part, dryRun and nothing else to read the part as it is: its sections " +
        "and version_tokens, or its values. Customers see the change only when the theme is published (publish_theme). Returns " +
        "the changes; call it with dryRun first to show them. For templates use edit_template, for template rules edit_template_rules.")]
    public async Task<ThemeFileChange> EditThemeDraft(
        RequestContext<CallToolRequestParams> context,
        [Description("Store id.")] string store_id,
        [Description("What to edit: header, footer, brand or palette.")] string part,
        [Description("header and footer: the version token of the part's file, from version_tokens of the last answer. An edit of an older version is refused.")] string version_token = null,
        [Description("header and footer: the changes of sections and blocks, applied in order, as in edit_page_draft.")] PageEditOperation[] operations = null,
        [Description("brand: URL of the logo on light backgrounds; an empty text removes it.")] string logo_url = null,
        [Description("brand: URL of the logo on dark backgrounds (the footer, the dark theme); an empty text removes it.")] string secondary_logo_url = null,
        [Description("brand: URL of the favicon; an empty text removes it.")] string favicon_url = null,
        [Description("palette: the main brand color, #rrggbb, to generate the whole light and dark palette from. It replaces every color of both.")] string primary_color = null,
        [Description("palette: a second brand color, #rrggbb, for the generation; without it one is derived from primary_color.")] string secondary_color = null,
        [Description("palette: light colors to set by token, for example { \"color_primary_500\": \"#0a5f38\" }; null removes one, so the storefront's preset shows. Applied after a generation.")] Dictionary<string, string> light_colors = null,
        [Description("palette: dark colors to set by token, as light_colors.")] Dictionary<string, string> dark_colors = null,
        [Description("true to check the edit and return what it would change, without saving it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(store_id))
        {
            throw new McpException("Pass store_id: the theme belongs to a store.");
        }

        await authorizationService.EnsureAllowedAsync(context.User, new SiteBuilderPageSearchCriteria { StoreId = store_id }, store_id);

        var name = part?.Trim().ToLowerInvariant();
        CheckArguments(name, operations, logo_url, secondary_logo_url, favicon_url, primary_color, secondary_color, light_colors, dark_colors);
        var change = new ThemeFileChange { Action = "edit_theme_draft", ObjectType = "theme-part", StoreId = store_id };
        var request = new PartRequest(context.User, store_id, operations, dryRun, cancellationToken);
        var edits = name switch
        {
            HeaderPart or FooterPart => await EditGroupAsync(request, name, version_token, change),
            BrandPart => await EditBrandAsync(request, new Dictionary<string, string>
            {
                ["logoUrl"] = logo_url,
                ["secondaryLogoUrl"] = secondary_logo_url,
                ["faviconUrl"] = favicon_url,
            }, version_token, change),
            PalettePart => await EditPaletteAsync(request, primary_color, secondary_color, light_colors, dark_colors, version_token, change),
            _ => throw new McpException("part must be header, footer, brand or palette."),
        };

        // Only reading: the part as it is, with its versions.
        if (edits == null)
        {
            change.DryRun = true;
            return change;
        }

        var catalogHash = await ThemeFiles.RequireCatalogHashAsync(schemaCatalogService, store_id, cancellationToken);
        await ThemeFileEdits.SaveAsync(change, designerOperationService, context.User, store_id, catalogHash, edits, dryRun, Theme,
            $"Call edit_theme_draft with part '{name}', dryRun and nothing else to read it again, then repeat the edit.", cancellationToken);

        return change;
    }

    private async Task<IList<ThemeFileEdit>> EditGroupAsync(PartRequest request, string group, string versionToken, ThemeFileChange change)
    {
        var path = await ThemeFiles.GetPathAsync(themeStorageFactory, request.StoreId, group == HeaderPart ? ThemeStorage.HeaderGroupFile : ThemeStorage.FooterGroupFile);
        var current = await ThemeFiles.ReadAsync(designerOperationService, request.User, request.StoreId, path, request.CancellationToken);
        Describe(change, path, group == HeaderPart ? "The header" : "The footer", current);

        // A theme without the file still has a header and a footer, which start empty, as the designer saves them.
        var content = current.Content ?? new JsonObject { ["type"] = group, ["name"] = group == HeaderPart ? "Header" : "Footer", ["content"] = new JsonArray() }.ToJsonString();
        var editor = PageDocumentEditor.Read(content, PageDocumentKind.SectionGroup)
            ?? throw new McpException($"The {group} of store '{request.StoreId}' is not a section group, so it cannot be edited here. Open it in the designer.");
        change.Sections = SectionRow.From(editor.Sections);

        if (request.Operations == null || request.Operations.Length == 0)
        {
            return null;
        }

        if (request.Operations.Length > MaxOperations)
        {
            throw new McpException($"Pass at most {MaxOperations} operations in one call.");
        }

        if (string.IsNullOrWhiteSpace(versionToken) || versionToken.Trim() != current.ETag)
        {
            throw new McpException($"The {group} changed since you read it, or version_token is not its version. Call edit_theme_draft with part " +
                $"'{group}', dryRun and nothing else to read it, and make the edit with its version token.");
        }

        var sources = new PageEditSources(authorizationService, groupedPageService, pageContentService, sharedComponentService,
            sharedComponentContentService, request.User, request.StoreId);
        await editor.ApplyAsync(request.Operations, sources, request.CancellationToken);
        change.Sections = SectionRow.From(editor.Sections);

        return [new ThemeFileEdit(path, current, editor.Write())];
    }

    private async Task<IList<ThemeFileEdit>> EditBrandAsync(PartRequest request, Dictionary<string, string> values, string versionToken, ThemeFileChange change)
    {
        var path = await ThemeFiles.GetPathAsync(themeStorageFactory, request.StoreId, ThemeStorage.BrandFile);
        var current = await ThemeFiles.ReadAsync(designerOperationService, request.User, request.StoreId, path, request.CancellationToken);
        Describe(change, path, "The brand", current);
        CheckVersion(versionToken, current, BrandPart);

        var brand = await ReadBrandAsync(request, current);
        change.Values = (JsonObject)brand.DeepClone();
        if (values.Values.All(x => x == null))
        {
            return null;
        }

        foreach (var (field, value) in values.Where(x => x.Value != null))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                brand.Remove(field);
            }
            else
            {
                brand[field] = value.Trim();
            }
        }

        change.Values = (JsonObject)brand.DeepClone();

        return [new ThemeFileEdit(path, current, brand.ToJsonString(PageDocumentEditor.WriteOptions))];
    }

    // The brand file; a theme without one shows the logos of its settings, which the first brand edit carries over.
    private async Task<JsonObject> ReadBrandAsync(PartRequest request, ThemeFileContent current)
    {
        if (ParseObject(current.Content) is { } file)
        {
            return new JsonObject(BrandFields
                .Where(x => !string.IsNullOrWhiteSpace(GetString(file[x.Field])))
                .Select(x => KeyValuePair.Create(x.Field, (JsonNode)GetString(file[x.Field]))));
        }

        var settingsPath = await ThemeFiles.GetPathAsync(themeStorageFactory, request.StoreId, ThemeStorage.SettingsDataFile);
        var settings = CurrentSettings(ParseObject((await ThemeFiles.ReadAsync(designerOperationService, request.User, request.StoreId, settingsPath,
            request.CancellationToken)).Content));

        return new JsonObject(BrandFields
            .Where(x => !string.IsNullOrWhiteSpace(GetString(settings?[x.SettingsKey])))
            .Select(x => KeyValuePair.Create(x.Field, (JsonNode)GetString(settings[x.SettingsKey]))));
    }

    private async Task<IList<ThemeFileEdit>> EditPaletteAsync(PartRequest request, string primary, string secondary, Dictionary<string, string> lightColors,
        Dictionary<string, string> darkColors, string versionToken, ThemeFileChange change)
    {
        var lightPath = await ThemeFiles.GetPathAsync(themeStorageFactory, request.StoreId, ThemeStorage.PaletteFile);
        var darkPath = await ThemeFiles.GetPathAsync(themeStorageFactory, request.StoreId, ThemeStorage.DarkPaletteFile);
        var light = await ThemeFiles.ReadAsync(designerOperationService, request.User, request.StoreId, lightPath, request.CancellationToken);
        var dark = await ThemeFiles.ReadAsync(designerOperationService, request.User, request.StoreId, darkPath, request.CancellationToken);
        Describe(change, lightPath, "The palette", light);
        change.VersionTokens[darkPath] = dark.ETag;
        CheckVersion(versionToken, light, PalettePart);

        var catalog = await ThemeFiles.GetCatalogAsync(schemaCatalogService, request.StoreId, request.CancellationToken);
        var tokens = ThemeFiles.GetPaletteTokens(catalog);
        var colors = (Light: ReadColors(light.Content), Dark: ReadColors(dark.Content));

        var reading = primary == null && secondary == null && lightColors == null && darkColors == null;
        if (!reading)
        {
            if (primary != null || secondary != null)
            {
                colors = Generate(primary, secondary, tokens);
                change.AddWarning("The palette was generated from the brand colors: it replaces every light and dark color the theme had.");
            }

            SetColors(colors.Light, lightColors, tokens.Light, "light", change);
            SetColors(colors.Dark, darkColors, tokens.Dark, "dark", change);
        }

        change.Values = new JsonObject { ["light"] = ToJson(colors.Light), ["dark"] = ToJson(colors.Dark) };
        CheckContrast(colors.Light, colors.Dark, tokens, change);

        if (reading)
        {
            return null;
        }

        return
        [
            new ThemeFileEdit(lightPath, light, ToJson(colors.Light).ToJsonString(PageDocumentEditor.WriteOptions)),
            new ThemeFileEdit(darkPath, dark, ToJson(colors.Dark).ToJsonString(PageDocumentEditor.WriteOptions)),
        ];
    }

    private (Dictionary<string, string> Light, Dictionary<string, string> Dark) Generate(string primary, string secondary, ThemePaletteTokens tokens)
    {
        if (!PaletteColor.TryParse(primary, out _))
        {
            throw new McpException("primary_color must be a color as #rrggbb or #rgb.");
        }

        if (!string.IsNullOrEmpty(secondary) && !PaletteColor.TryParse(secondary, out _))
        {
            throw new McpException("secondary_color must be a color as #rrggbb or #rgb.");
        }

        if (tokens.Light.Count == 0)
        {
            throw new McpException("The storefront has not announced its palette tokens, so a palette cannot be generated. Open the store once in " +
                "the designer (its preview sends them), or set colors with light_colors.");
        }

        var generated = themePaletteService.GeneratePalette(new ThemePaletteGenerationRequest { Primary = primary, Secondary = secondary, Tokens = tokens });

        return (new Dictionary<string, string>(generated.Light ?? new Dictionary<string, string>()), new Dictionary<string, string>(generated.Dark ?? new Dictionary<string, string>()));
    }

    private static void SetColors(Dictionary<string, string> colors, Dictionary<string, string> changes, IList<string> announced, string scheme, ThemeFileChange change)
    {
        foreach (var (token, color) in changes ?? [])
        {
            if (!ThemePaletteValues.IsTokenName(token))
            {
                throw new McpException($"'{token}' is not a palette token: tokens are lowercase letters, digits and underscores, such as color_primary_500.");
            }

            if (color == null)
            {
                colors.Remove(token);
                continue;
            }

            if (!ThemePaletteValues.IsColor(color.Trim()))
            {
                throw new McpException($"The {scheme} color of {token}, '{color}', is not a color such as #0a5f38 or rgb(10 95 56).");
            }

            colors[token] = color.Trim();
            if (announced.Count > 0 && !announced.Contains(token))
            {
                change.AddWarning($"The storefront does not announce the {scheme} token {token}, so it may not use the color.");
            }
        }

        if (colors.Count > ThemePaletteTokensValidator.MaxTokens)
        {
            throw new McpException($"A palette has at most {ThemePaletteTokensValidator.MaxTokens} colors per color scheme.");
        }
    }

    // As the publication checks it: light colors are needed for any check, and the storefront then gets both schemes.
    private void CheckContrast(Dictionary<string, string> light, Dictionary<string, string> dark, ThemePaletteTokens tokens, ThemeFileChange change)
    {
        if (light.Count == 0)
        {
            return;
        }

        var result = themePaletteService.CheckPalette(new ThemePaletteCheckRequest
        {
            Palette = new ThemePalette { Light = light, Dark = dark.Count > 0 ? dark : null },
            Tokens = tokens,
        });

        change.ContrastFailures = result.Failures.Select(ContrastFailureRow.From).ToList();
        foreach (var failure in change.ContrastFailures)
        {
            change.AddWarning($"{failure.Describe()}. The theme cannot be published with this pair: change one of its colors.");
        }
    }

    private static void Describe(ThemeFileChange change, string path, string name, ThemeFileContent current)
    {
        change.ObjectId = path;
        change.ObjectName = name;
        change.VersionTokens[path] = current.ETag;
    }

    private static void CheckVersion(string versionToken, ThemeFileContent current, string part)
    {
        if (!string.IsNullOrWhiteSpace(versionToken) && versionToken.Trim() != current.ETag)
        {
            throw new McpException($"The {part} changed since you read it. Call edit_theme_draft with part '{part}', dryRun and nothing else to read it, then repeat the edit.");
        }
    }

    // Arguments of another part are refused rather than ignored, so the agent learns that nothing of them was saved.
    private static void CheckArguments(string part, PageEditOperation[] operations, string logo, string secondaryLogo, string favicon, string primary,
        string secondary, Dictionary<string, string> light, Dictionary<string, string> dark)
    {
        if (part is not (HeaderPart or FooterPart or BrandPart or PalettePart))
        {
            throw new McpException("part must be header, footer, brand or palette.");
        }

        var misplaced = new List<string>();
        if (part is not (HeaderPart or FooterPart) && operations?.Length > 0)
        {
            misplaced.Add("operations");
        }

        if (part != BrandPart && (logo ?? secondaryLogo ?? favicon) != null)
        {
            misplaced.Add("logo_url, secondary_logo_url, favicon_url");
        }

        if (part != PalettePart && ((primary ?? secondary) != null || light != null || dark != null))
        {
            misplaced.Add("primary_color, secondary_color, light_colors, dark_colors");
        }

        if (misplaced.Count > 0)
        {
            throw new McpException($"{string.Join("; ", misplaced)} do not apply to part '{part}', so nothing was saved.");
        }
    }

    private static Dictionary<string, string> ReadColors(string content) =>
        ParseObject(content)?
            .Where(x => GetString(x.Value) != null)
            .ToDictionary(x => x.Key, x => GetString(x.Value), StringComparer.Ordinal)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static JsonObject ToJson(Dictionary<string, string> colors) =>
        new(colors.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, (JsonNode)x.Value)));

    // The settings a theme's settings_data.json holds now: its current preset's, or the current object itself.
    private static JsonObject CurrentSettings(JsonObject data)
    {
        if (data == null)
        {
            return null;
        }

        var name = GetString(data["current"]);
        var current = name != null && data["presets"] is JsonObject presets ? presets[name] as JsonObject : data["current"] as JsonObject;

        return name != null && current?["settings"] is JsonObject settings ? settings : current;
    }

    private static JsonObject ParseObject(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(content) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record PartRequest(ClaimsPrincipal User, string StoreId, PageEditOperation[] Operations, bool DryRun, CancellationToken CancellationToken);
}
