namespace VirtoCommerce.SiteBuilder.Core.Theme;

public interface IThemePaletteService
{
    ThemePaletteGenerationResult GeneratePalette(ThemePaletteGenerationRequest request);

    ThemePaletteCheckResult CheckPalette(ThemePaletteCheckRequest request);
}
