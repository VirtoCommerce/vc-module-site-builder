using Microsoft.Extensions.Configuration;

namespace VirtoCommerce.SiteBuilder.Core.Extensions;

public static class SiteBuilderConfigurationExtensions
{
    public const string SectionPrefix = "SiteBuilder:";
    public const string PageBuilderSectionPrefix = "PageBuilder:";

    /// <summary>Falls back to Page Builder's section, so an installation keeps its configuration after the switch.</summary>
    public static IConfigurationSection GetSiteBuilderSection(this IConfiguration configuration, string sectionName)
    {
        return configuration.GetSiteBuilderSection(sectionName, out _);
    }

    public static IConfigurationSection GetSiteBuilderSection(this IConfiguration configuration, string sectionName, out bool isPageBuilderSection)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(sectionName);

        var section = configuration.GetSection(sectionName);
        isPageBuilderSection = false;

        if (HasContent(section) || !sectionName.StartsWith(SectionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return section;
        }

        var pageBuilderSection = configuration.GetSection(GetPageBuilderSectionName(sectionName));
        if (!HasContent(pageBuilderSection))
        {
            return section;
        }

        isPageBuilderSection = true;
        return pageBuilderSection;
    }

    public static string GetPageBuilderSectionName(string sectionName)
    {
        return PageBuilderSectionPrefix + sectionName[SectionPrefix.Length..];
    }

    private static bool HasContent(IConfigurationSection section)
    {
        return !string.IsNullOrEmpty(section.Value) || section.GetChildren().Any();
    }
}
