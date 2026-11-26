using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

/// <param name="Path">The type's file, with the theme's name.</param>
/// <param name="Warning">What the publication does to the type and to the pages that place it.</param>
public sealed record RemovedSectionType(string Path, string Warning)
{
    public const string PublishedStatus = "published";
    public const string RemovedStatus = "removed";
}

/// <summary>The custom section types live now that the theme's next publication leaves out.</summary>
public class RemovedSectionTypes(
    IThemeStorageFactory themeStorageFactory,
    ICustomSectionService customSectionService,
    SectionTypePages sectionTypePages)
{
    // A type is published from its file, so one live now whose file is gone, deleted in storage or by restoring an older theme, is left out.
    public async Task<IList<RemovedSectionType>> FindAsync(ClaimsPrincipal user, string storeId, CancellationToken cancellationToken)
    {
        var storage = await themeStorageFactory.OpenAsync(storeId);
        var live = await ThemeCustomSections.GetPublishedTypesAsync(storage);
        if (live.Count == 0)
        {
            return [];
        }

        var kept = (await customSectionService.ListTypesAsync(storeId, storage.ThemeName, cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var removed = new List<RemovedSectionType>();
        foreach (var (type, content) in live.Where(x => !kept.Contains(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var name = ThemeCustomSections.GetName(content?[ThemeCustomSections.NameKey], language: null) ?? type;
            var pages = await sectionTypePages.DescribePublishedAndDraftsAsync(user, storeId, type, cancellationToken);
            removed.Add(new RemovedSectionType($"{storage.ThemeName}/{ThemeStorage.GetCustomSectionFile(type)}",
                $"Section type '{CustomSectionText.Quote(name)}' ({type}) will be removed: it is live now, but its file is no longer in the theme, so this " +
                $"publication leaves it out. {pages}"));
        }

        return removed;
    }
}
