namespace VirtoCommerce.SiteBuilder.Core.Theme;

public class ThemeSnapshotOptions
{
    public const string SectionName = "SiteBuilder:ThemeSnapshot";

    private const int BytesInKilobyte = 1024;
    private const int DefaultSizeWarningKilobytes = 30;
    private const int DefaultCustomSectionsKilobytes = 256;

    /// <summary>A shorter platform cache expiration is kept.</summary>
    public TimeSpan CacheAbsoluteExpiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>In gzip bytes.</summary>
    public int SizeWarningThreshold { get; set; } = DefaultSizeWarningKilobytes * BytesInKilobyte;

    /// <summary>In bytes of JSON, for all the custom section types of a theme together: the snapshot carries every type to every page.</summary>
    public int CustomSectionsMaxSize { get; set; } = DefaultCustomSectionsKilobytes * BytesInKilobyte;
}
