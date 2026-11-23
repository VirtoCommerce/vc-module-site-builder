using System.Globalization;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

public readonly record struct PaletteColor(byte R, byte G, byte B)
{
    private const int RedShift = 16;
    private const int GreenShift = 8;
    private const int ChannelMask = 0xff;
    private const double ChannelMax = byte.MaxValue;

    // the hue circle in six sectors of 60 degrees: red, yellow, green, cyan, blue, magenta
    private const double FullCircle = 360;
    private const double SectorDegrees = 60;
    private const int SectorCount = 6;
    private const int RedSector = 0;
    private const int YellowSector = 1;
    private const int GreenSector = 2;
    private const int CyanSector = 3;
    private const int BlueSector = 4;

    public static PaletteColor White { get; } = new(byte.MaxValue, byte.MaxValue, byte.MaxValue);

    public static PaletteColor Black { get; } = new(0, 0, 0);

    public static bool TryParse(string value, out PaletteColor color)
    {
        color = default;

        var hex = value?.Trim();
        if (string.IsNullOrEmpty(hex) || hex[0] != '#')
        {
            return false;
        }

        hex = hex[1..];
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(x => new string(x, 2)));
        }

        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = new PaletteColor((byte)(rgb >> RedShift), (byte)((rgb >> GreenShift) & ChannelMask), (byte)(rgb & ChannelMask));
        return true;
    }

    /// <param name="hue">Degrees, 0–360.</param>
    /// <param name="saturation">0–1.</param>
    /// <param name="lightness">0–1.</param>
    public static PaletteColor FromHsl(double hue, double saturation, double lightness)
    {
        saturation = Math.Clamp(saturation, 0, 1);
        lightness = Math.Clamp(lightness, 0, 1);

        var chroma = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        var sector = ((hue % FullCircle) + FullCircle) % FullCircle / SectorDegrees;
        var second = chroma * (1 - Math.Abs((sector % 2) - 1));
        var (r, g, b) = (int)sector switch
        {
            RedSector => (chroma, second, 0d),
            YellowSector => (second, chroma, 0d),
            GreenSector => (0d, chroma, second),
            CyanSector => (0d, second, chroma),
            BlueSector => (second, 0d, chroma),
            _ => (chroma, 0d, second),
        };
        var offset = lightness - (chroma / 2);

        return new PaletteColor(ToByte(r + offset), ToByte(g + offset), ToByte(b + offset));
    }

    public (double Hue, double Saturation, double Lightness) ToHsl()
    {
        // bytes compare exactly, unlike the doubles made from them
        var maxChannel = Math.Max(R, Math.Max(G, B));
        var minChannel = Math.Min(R, Math.Min(G, B));
        var r = R / ChannelMax;
        var g = G / ChannelMax;
        var b = B / ChannelMax;
        var max = maxChannel / ChannelMax;
        var min = minChannel / ChannelMax;
        var lightness = (max + min) / 2;
        var chroma = max - min;

        if (maxChannel == minChannel)
        {
            return (0, 0, lightness);
        }

        var saturation = chroma / (1 - Math.Abs((2 * lightness) - 1));
        double hue;
        if (maxChannel == R)
        {
            hue = SectorDegrees * ((g - b) / chroma % SectorCount);
        }
        else if (maxChannel == G)
        {
            hue = SectorDegrees * (((b - r) / chroma) + GreenSector);
        }
        else
        {
            hue = SectorDegrees * (((r - g) / chroma) + BlueSector);
        }

        return (hue < 0 ? hue + FullCircle : hue, saturation, lightness);
    }

    public string ToHex() => string.Create(CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}");

    public override string ToString() => ToHex();

    private static byte ToByte(double channel) => (byte)Math.Round(Math.Clamp(channel, 0, 1) * ChannelMax, MidpointRounding.AwayFromZero);
}
