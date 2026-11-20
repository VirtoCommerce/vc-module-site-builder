namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>WCAG 2.2 relative luminance and contrast ratio.</summary>
public static class ColorContrast
{
    private const double RedWeight = 0.2126;
    private const double GreenWeight = 0.7152;
    private const double BlueWeight = 0.0722;

    // added to both luminances, for the flare of a real screen
    private const double Flare = 0.05;

    // the sRGB transfer function
    private const double LinearSegmentLimit = 0.04045;
    private const double LinearSegmentSlope = 12.92;
    private const double GammaOffset = 0.055;
    private const double GammaScale = 1.055;
    private const double GammaExponent = 2.4;

    public static double GetRelativeLuminance(PaletteColor color) =>
        (RedWeight * Linearize(color.R)) + (GreenWeight * Linearize(color.G)) + (BlueWeight * Linearize(color.B));

    public static double GetContrastRatio(PaletteColor first, PaletteColor second)
    {
        var firstLuminance = GetRelativeLuminance(first);
        var secondLuminance = GetRelativeLuminance(second);

        return (Math.Max(firstLuminance, secondLuminance) + Flare) / (Math.Min(firstLuminance, secondLuminance) + Flare);
    }

    private static double Linearize(byte channel)
    {
        var value = channel / 255d;
        return value <= LinearSegmentLimit ? value / LinearSegmentSlope : Math.Pow((value + GammaOffset) / GammaScale, GammaExponent);
    }
}
