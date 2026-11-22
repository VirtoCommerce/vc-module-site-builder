using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core.Common;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Data.Services;

[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The services come from dependency injection; a parameter object would only hide them and change the constructor that subclasses call.")]
public class DesignerContentService(
    IThemeDraftService themeDraftService,
    IThemePublisher themePublisher,
    ISiteBuilderPageService pageService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService groupedPageContentService,
    IGroupedPageContentWriter groupedPageContentWriter,
    IFilePageDraftService filePageDraftService,
    IContentHistory contentHistory,
    ISiteBuilderContentGuard contentGuard,
    ISchemaCatalogService schemaCatalogService,
    ICustomSectionService customSectionService,
    ICustomSectionValidator customSectionValidator) : IDesignerContentService
{
    private const string NeverPublishedError = "This page has never been published, so there is no published version to go back to.";
    private const string PageName = "page";

    public virtual Task<ThemeFilesValidationResult> ValidateThemeFilesAsync(string storeId, IDictionary<string, JToken> files, bool draft, string catalogHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        return ValidateThemeFilesInternalAsync(storeId, files, draft, catalogHash, cancellationToken);
    }

    private async Task<ThemeFilesValidationResult> ValidateThemeFilesInternalAsync(string storeId, IDictionary<string, JToken> files, bool draft,
        string catalogHash, CancellationToken cancellationToken)
    {
        // the theme is published as a whole, from drafts
        if (!draft)
        {
            throw new InvalidDataException("Theme files are saved as drafts and published with theme/publish.");
        }

        var catalog = await GetCatalogAsync(storeId, catalogHash, cancellationToken);
        // each theme folder on its own: two themes may hold a file of the same name
        var themes = new Dictionary<string, IDictionary<string, JToken>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, content) in files)
        {
            var themeName = SiteBuilderContentPaths.GetThemeName(path);
            if (!themes.TryGetValue(themeName, out var contents))
            {
                themes[themeName] = contents = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
            }

            contents[SiteBuilderContentPaths.GetPathInTheme(path)] = content;
        }

        var customSections = await GetCustomSectionsAsync(storeId, files, cancellationToken);
        var catalogs = customSections.ToDictionary(x => x.Key, x => ThemeCustomSections.AddToCatalog(catalog, x.Value), StringComparer.OrdinalIgnoreCase);
        var customSectionResults = ValidateCustomSections(files, customSections, catalog);
        await DropSizeErrorsOfSavesThatDoNotGrowAsync(storeId, customSections, customSectionResults, cancellationToken);
        var errors = files
            .SelectMany(x => contentGuard.GuardThemeFile(SiteBuilderContentPaths.GetPathInTheme(x.Key), x.Value, catalogs[SiteBuilderContentPaths.GetThemeName(x.Key)]).Errors)
            .Concat(customSectionResults.Values.SelectMany(ThemeCustomSections.GetErrors))
            .Concat(files.SelectMany(x => GetSectionGroupErrors(x.Key, x.Value)))
            .ToList();
        if (errors.Count > 0)
        {
            throw new ThemeValidationException(errors);
        }

        var mainSectionErrors = await GetMainSectionErrorsAsync(storeId, files, cancellationToken);
        if (mainSectionErrors.Count > 0)
        {
            throw new ThemeValidationException([.. mainSectionErrors]);
        }

        var sectionErrors = new List<string>();
        foreach (var theme in files.GroupBy(x => SiteBuilderContentPaths.GetThemeName(x.Key), StringComparer.OrdinalIgnoreCase))
        {
            sectionErrors.AddRange(await GetSectionErrorsAsync(storeId, theme.ToDictionary(x => x.Key, x => x.Value), catalogs[theme.Key], cancellationToken));
        }

        if (sectionErrors.Count > 0)
        {
            throw new ThemeValidationException([.. sectionErrors]);
        }

        var result = AbstractTypeFactory<ThemeFilesValidationResult>.TryCreateInstance();
        foreach (var (themeName, results) in customSectionResults)
        {
            foreach (var typeResult in results.Where(x => x.Warnings.Count > 0))
            {
                result.Warnings[$"{themeName}/{typeResult.File}"] = typeResult.Warnings;
            }
        }

        if (string.IsNullOrEmpty(catalogHash))
        {
            return result;
        }

        foreach (var (themeName, contents) in themes)
        {
            // a template may place the theme's other types; ValidateFilesAsync does not check their own rules
            foreach (var (type, content) in customSections[themeName])
            {
                contents.TryAdd(ThemeStorage.GetCustomSectionFile(type), content);
            }

            await themePublisher.ValidateFilesAsync(storeId, catalogHash, contents, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// By theme folder, for each folder the files are in: the theme's custom section types with those the files write over them. They are
    /// read only for a folder whose files write a type or place one.
    /// </summary>
    /// <param name="files">Contents by the path with the theme folder.</param>
    protected virtual async Task<IDictionary<string, IDictionary<string, JToken>>> GetCustomSectionsAsync(string storeId, IDictionary<string, JToken> files,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IDictionary<string, JToken>>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in files.GroupBy(x => SiteBuilderContentPaths.GetThemeName(x.Key), StringComparer.OrdinalIgnoreCase))
        {
            result[theme.Key] = theme.Any(x => IsCustomSectionFile(SiteBuilderContentPaths.GetPathInTheme(x.Key)) || ThemeCustomSections.PlacesCustomType(x.Value))
                ? await customSectionService.GetTypesAsync(storeId, theme.Key, files, cancellationToken)
                : new Dictionary<string, JToken>();
        }

        return result;
    }

    /// <summary>By theme folder: the rules of the types the files write, and the size of all the theme's types once written.</summary>
    /// <param name="files">Contents by the path with the theme folder.</param>
    /// <param name="customSections">By theme folder, as <see cref="GetCustomSectionsAsync"/> reads them.</param>
    protected virtual IDictionary<string, IList<CustomSectionValidationResult>> ValidateCustomSections(IDictionary<string, JToken> files,
        IDictionary<string, IDictionary<string, JToken>> customSections, JObject catalog)
    {
        var result = new Dictionary<string, IList<CustomSectionValidationResult>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (themeName, types) in customSections)
        {
            var written = files.Keys
                .Where(x => string.Equals(SiteBuilderContentPaths.GetThemeName(x), themeName, StringComparison.OrdinalIgnoreCase))
                .Select(x => ThemeCustomSections.GetCustomSectionType(SiteBuilderContentPaths.GetPathInTheme(x)))
                .Where(x => x != null)
                .ToList();
            result[themeName] = written.Count == 0 ? [] : customSectionValidator.ValidateTypes(types, written, catalog);
        }

        return result;
    }

    /// <summary>
    /// A save that does not grow the theme's types is not refused on their size, so types over the limit, after two saves at once or a lower
    /// limit, can still shrink; the publication holds the limit as it is. The types before the save are read only for a theme over it.
    /// </summary>
    protected virtual async Task DropSizeErrorsOfSavesThatDoNotGrowAsync(string storeId, IDictionary<string, IDictionary<string, JToken>> customSections,
        IDictionary<string, IList<CustomSectionValidationResult>> results, CancellationToken cancellationToken)
    {
        foreach (var (themeName, themeResults) in results)
        {
            if (themeResults.FirstOrDefault(x => x.File == ThemeStorage.CustomSectionsFolder) is not { } sizeResult)
            {
                continue;
            }

            var before = await customSectionService.GetTypesAsync(storeId, themeName, files: null, cancellationToken);
            if (customSectionValidator.GetSize(customSections[themeName]) <= customSectionValidator.GetSize(before))
            {
                themeResults.Remove(sizeResult);
            }
        }
    }

    /// <summary>
    /// The catalog to guard a page with, and one per theme folder for the theme files: a type just written is in no catalog the preview
    /// sent, so each has its theme's types when its content places one.
    /// </summary>
    /// <param name="themeFiles">Contents by the path with the theme folder.</param>
    protected virtual async Task<(JObject Page, IDictionary<string, JObject> Themes)> GetGuardCatalogsAsync(string storeId, JObject catalog,
        bool pagePlacesCustomType, IDictionary<string, JToken> themeFiles, IDictionary<string, IDictionary<string, JToken>> customSections,
        CancellationToken cancellationToken)
    {
        var page = catalog != null && pagePlacesCustomType
            ? ThemeCustomSections.AddToCatalog(catalog, await customSectionService.GetTypesAsync(storeId, themeName: null, themeFiles, cancellationToken))
            : catalog;
        var themes = customSections.ToDictionary(x => x.Key, x => x.Value.Count == 0 ? catalog : ThemeCustomSections.AddToCatalog(catalog, x.Value),
            StringComparer.OrdinalIgnoreCase);

        return (page, themes);
    }

    /// <param name="file">The path with the theme folder.</param>
    private static IEnumerable<string> GetSectionGroupErrors(string file, JToken content)
    {
        var pathInTheme = SiteBuilderContentPaths.GetPathInTheme(file);

        return ThemeCustomSections.IsSectionGroupFile(pathInTheme) ? ThemeCustomSections.GetSectionGroupErrors(pathInTheme, content) : [];
    }

    private static bool PlacesCustomType(string content) =>
        content != null && content.Contains("custom-", StringComparison.Ordinal) && ThemeCustomSections.PlacesCustomType(ParseOrNull(content));

    private static bool IsCustomSectionFile(string file) => ThemeCustomSections.GetCustomSectionType(file) != null;

    /// <summary>
    /// A template draft may not lose the main section the template shows: removed, or hidden where it was not hidden already.
    /// A template without one, such as a theme's empty default, stays as it is: the storefront adds a missing main section itself.
    /// </summary>
    /// <param name="files">Contents by the path with the theme folder.</param>
    protected virtual async Task<IList<string>> GetMainSectionErrorsAsync(string storeId, IDictionary<string, JToken> files, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var (path, content) in files)
        {
            var file = SiteBuilderContentPaths.GetPathInTheme(path);
            var mainSection = ThemeTemplateMainSections.GetMainSection(ThemeTemplateMainSections.GetTemplateKey(file));
            if (mainSection == null || ThemeTemplateMainSections.GetState(content, mainSection) is not { } state || state == ThemeTemplateMainSectionState.Shown)
            {
                continue;
            }

            var current = GetMainSectionState(await themeDraftService.ReadAsync(storeId, path, cancellationToken), mainSection);
            if (state == ThemeTemplateMainSectionState.Missing && current is ThemeTemplateMainSectionState.Shown or ThemeTemplateMainSectionState.Hidden)
            {
                errors.Add($"{file}: the template's main section '{mainSection}' cannot be removed");
            }
            else if (state == ThemeTemplateMainSectionState.Hidden && current != ThemeTemplateMainSectionState.Hidden)
            {
                errors.Add($"{file}: the template's main section '{mainSection}' cannot be hidden");
            }
        }

        return errors;
    }

    /// <summary><c>null</c> for no file, or one that is not a page document.</summary>
    protected virtual ThemeTemplateMainSectionState? GetMainSectionState(ThemeFileContent file, string mainSection) =>
        ParseOrNull(file) is { } document ? ThemeTemplateMainSections.GetState(document, mainSection) : null;

    /// <summary>
    /// A template draft may not gain a section the designer does not offer its template, such as one its template schema does not list.
    /// A section the template held already stays: the storefront shows it, and the designer keeps it.
    /// </summary>
    /// <param name="files">Contents by the path with the theme folder.</param>
    protected virtual async Task<IList<string>> GetSectionErrorsAsync(string storeId, IDictionary<string, JToken> files, JObject catalog,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        if (catalog == null)
        {
            return errors;
        }

        foreach (var (path, content) in files)
        {
            var file = SiteBuilderContentPaths.GetPathInTheme(path);
            var templateType = ThemeTemplateMainSections.GetTemplateKey(file)?.Split('.')[0];
            var refused = templateType == null
                ? []
                : ThemeTemplateSections.GetSections(content)
                    .Where(x => x.Type != ModuleConstants.SharedComponents.ReferenceType && !ThemeTemplateSections.IsOffered(catalog, templateType, x.Type))
                    .ToList();
            if (refused.Count == 0)
            {
                continue;
            }

            var held = ThemeTemplateSections.GetSections(ParseOrNull(await themeDraftService.ReadAsync(storeId, path, cancellationToken)));
            errors.AddRange(refused.Except(held).Select(x => $"{file}: a {templateType} template cannot hold the section '{x.Id}' of type '{x.Type}'"));
        }

        return errors;
    }

    private static JToken ParseOrNull(ThemeFileContent file) => ParseOrNull(file?.Content);

    private static JToken ParseOrNull(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        try
        {
            return ThemeJson.Parse(content);
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }

    public virtual Task<DesignerSaveResult> SaveAsync(DesignerSaveCommand command, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return SaveInternalAsync(command, user, cancellationToken);
    }

    private async Task<DesignerSaveResult> SaveInternalAsync(DesignerSaveCommand command, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await GuardAsync(command, cancellationToken);

        var result = AbstractTypeFactory<DesignerSaveResult>.TryCreateInstance();
        result.SaveId = command.SaveId;
        result.ETag = await themeDraftService.SaveDraftsAsync(
            command.StoreId,
            command.ThemeFiles.ToDictionary(x => x.Path, x => x.Content),
            command.ThemeFiles.ToDictionary(x => x.Path, x => x.ETag),
            token => SavePageAsync(command, user, token),
            cancellationToken);
        result.ThemeFiles = command.ThemeFiles.Select(x => CreateThemeFileVersion(command.StoreId, x)).ToList();

        var record = CreateHistoryRecord(ModuleConstants.GitHistory.SavedSummary, command.SaveId, user);
        if (command.Group != null)
        {
            record.AddEntry(new HistoryItem(HistoryItemType.Page, command.Group.Id), command.PageContent);
        }

        foreach (var file in command.ThemeFiles)
        {
            record.AddEntry(HistoryItem.ForThemeFile(file.Path), file.Content);
        }

        await contentHistory.RecordAsync(command.StoreId, record, cancellationToken);

        return result;
    }

    public virtual Task<SiteBuilderChangePreview> PreviewSaveAsync(DesignerSaveCommand command, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return PreviewSaveInternalAsync(command, user, cancellationToken);
    }

    private async Task<SiteBuilderChangePreview> PreviewSaveInternalAsync(DesignerSaveCommand command, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await GuardAsync(command, cancellationToken);

        var preview = AbstractTypeFactory<SiteBuilderChangePreview>.TryCreateInstance();
        if (command.Group != null)
        {
            var current = await groupedPageContentService.LoadGroupContentAsync(command.Group, draft: true, cancellationToken);
            preview.Objects.Add(CreateContentChange(ModuleConstants.ChangePreview.Page, command.Group.Id, current.ETag, current.Content, command.PageContent));
        }
        else if (command.FilePage != null)
        {
            var current = await filePageDraftService.ReadDraftAsync(command.StoreId, command.FilePage.ContentType, command.FilePage.Path, user, cancellationToken);
            preview.Objects.Add(CreateContentChange(ModuleConstants.ChangePreview.FilePage, command.FilePage.Path,
                SiteBuilderContentVersion.CreateForFileContent(current), current, PageJson.Serialize(command.FilePage.Content)));
        }

        foreach (var file in command.ThemeFiles)
        {
            var current = await themeDraftService.ReadAsync(command.StoreId, file.Path, cancellationToken);
            preview.Objects.Add(CreateContentChange(ModuleConstants.ChangePreview.ThemeFile, file.Path, current.ETag, current.Content, file.Content));
        }

        return preview;
    }

    public virtual Task<SiteBuilderChangePreview> PreviewDiscardAsync(DesignerDiscardCommand command, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return PreviewDiscardInternalAsync(command, user, cancellationToken);
    }

    private async Task<SiteBuilderChangePreview> PreviewDiscardInternalAsync(DesignerDiscardCommand command, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        // the page as DiscardPageAsync takes it: the group, else the file page
        var preview = command.Group != null
            ? await PreviewDiscardPageInternalAsync(command.Group, cancellationToken)
            : AbstractTypeFactory<SiteBuilderChangePreview>.TryCreateInstance();
        if (command.Group == null && command.FilePage != null)
        {
            var (contentType, path) = (command.FilePage.ContentType, command.FilePage.Path);
            var draft = await filePageDraftService.PreviewDiscardDraftAsync(command.StoreId, contentType, path, user, cancellationToken);
            if (draft.Status == DraftDiscardStatus.OnlyCopy)
            {
                throw new InvalidDataException(NeverPublishedError);
            }

            if (draft.Status == DraftDiscardStatus.Discarded)
            {
                var eTag = SiteBuilderContentVersion.CreateForFilePage(command.StoreId, contentType, path, draft.DraftContent);
                preview.Objects.Add(CreateContentChange(ModuleConstants.ChangePreview.FilePage, path, eTag, draft.DraftContent, draft.PublishedContent));
            }
        }

        foreach (var path in command.ThemeFiles)
        {
            var draft = await themeDraftService.PreviewDiscardAsync(command.StoreId, path, cancellationToken);
            if (draft.Status == DraftDiscardStatus.Discarded)
            {
                var eTag = SiteBuilderContentVersion.CreateForThemeFile(command.StoreId, path, draft.DraftContent);
                preview.Objects.Add(CreateContentChange(ModuleConstants.ChangePreview.ThemeFile, path, eTag, draft.DraftContent, draft.PublishedContent));
            }
            else if (draft.Status == DraftDiscardStatus.OnlyCopy)
            {
                preview.Warnings.Add($"{path} has never been published: it keeps its draft.");
            }
        }

        return preview;
    }

    public virtual Task<SiteBuilderChangePreview> PreviewDiscardPageAsync(GroupedSiteBuilderPage group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        return PreviewDiscardPageInternalAsync(group, cancellationToken);
    }

    private async Task<SiteBuilderChangePreview> PreviewDiscardPageInternalAsync(GroupedSiteBuilderPage group, CancellationToken cancellationToken)
    {
        var publishedPage = GetPublishedPage(group);

        var preview = AbstractTypeFactory<SiteBuilderChangePreview>.TryCreateInstance();
        if (group.Pages.Any(x => x.Status == Draft))
        {
            var current = await groupedPageContentService.LoadGroupContentAsync(group, draft: true, cancellationToken);
            var publishedContent = await groupedPageService.LoadContent(publishedPage.Id, cancellationToken);
            preview.Objects.Add(CreateContentChange(ModuleConstants.ChangePreview.Page, group.Id, current.ETag, current.Content, publishedContent));
        }

        return preview;
    }

    public virtual Task<ContentSaveResult> DiscardAsync(DesignerDiscardCommand command, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return DiscardInternalAsync(command, user, cancellationToken);
    }

    private async Task<ContentSaveResult> DiscardInternalAsync(DesignerDiscardCommand command, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var (themeDrafts, publishedPageContent) = await DiscardDraftsAsync(command, user, cancellationToken);

        var record = CreateHistoryRecord(ModuleConstants.GitHistory.DiscardedSummary, command.SaveId, user);
        if (publishedPageContent != null)
        {
            record.AddEntry(new HistoryItem(HistoryItemType.Page, command.Group.Id), publishedPageContent);
        }

        foreach (var draft in themeDrafts.Where(x => x.Status == DraftDiscardStatus.Discarded))
        {
            record.AddEntry(HistoryItem.ForThemeFile(draft.Path), draft.PublishedContent);
        }

        if (record.Entries.Count > 0)
        {
            await contentHistory.RecordAsync(command.StoreId, record, cancellationToken);
        }

        var result = AbstractTypeFactory<ContentSaveResult>.TryCreateInstance();
        result.SaveId = command.SaveId;

        return result;
    }

    private async Task<(IList<DiscardedThemeDraft> ThemeDrafts, string PublishedPageContent)> DiscardDraftsAsync(DesignerDiscardCommand command, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        string publishedPageContent = null;
        var themeDrafts = await themeDraftService.DiscardDraftsAsync(
            command.StoreId,
            command.ThemeFiles,
            command.ThemeFileETags,
            async token => publishedPageContent = await DiscardPageAsync(command, user, token),
            cancellationToken);

        return (themeDrafts, publishedPageContent);
    }

    public virtual Task<ContentSaveResult> RestoreAsync(ContentRestoreCommand command, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return RestoreInternalAsync(command, user, cancellationToken);
    }

    private async Task<ContentSaveResult> RestoreInternalAsync(ContentRestoreCommand command, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await GuardAsync(command, cancellationToken);

        var themeFiles = command.Contents.Where(x => x.Key.Type == HistoryItemType.Theme).ToDictionary(x => x.Key.Key, x => x.Value);
        var pageContent = command.Contents.Where(x => x.Key.Type == HistoryItemType.Page).Select(x => x.Value).SingleOrDefault();
        var pageETag = command.Group == null ? null : GetExpectedETag(command, new HistoryItem(HistoryItemType.Page, command.Group.Id));
        // a version restored replaces the drafts as they are now, as the page's restore does, unless the caller names the versions it read
        await themeDraftService.SaveDraftsAsync(command.StoreId, themeFiles, await GetThemeFileETagsAsync(command, themeFiles.Keys, cancellationToken),
            token => RestorePageAsync(command.Group, pageContent, pageETag, token), cancellationToken);

        var record = CreateHistoryRecord(GetRestoredDescription(command.VersionId), command.SaveId, user);
        foreach (var (item, content) in command.Contents)
        {
            record.AddEntry(item, content);
        }

        await contentHistory.RecordAsync(command.StoreId, record, cancellationToken);

        var result = AbstractTypeFactory<ContentSaveResult>.TryCreateInstance();
        result.SaveId = command.SaveId;

        return result;
    }

    public virtual Task<SiteBuilderChangePreview> PreviewRestoreAsync(ContentRestoreCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return PreviewRestoreInternalAsync(command, cancellationToken);
    }

    private async Task<SiteBuilderChangePreview> PreviewRestoreInternalAsync(ContentRestoreCommand command, CancellationToken cancellationToken)
    {
        await GuardAsync(command, cancellationToken);

        var preview = AbstractTypeFactory<SiteBuilderChangePreview>.TryCreateInstance();
        foreach (var (item, content) in command.Contents)
        {
            if (item.Type == HistoryItemType.Page)
            {
                var current = await groupedPageContentService.LoadGroupContentAsync(command.Group, draft: true, cancellationToken);
                preview.Objects.Add(CreateContentChange(ModuleConstants.ChangePreview.Page, command.Group.Id, current.ETag, current.Content, content));
            }
            else
            {
                var current = await themeDraftService.ReadAsync(command.StoreId, item.Key, cancellationToken);
                preview.Objects.Add(CreateContentChange(ModuleConstants.ChangePreview.ThemeFile, item.Key, current.ETag, current.Content, content));
            }
        }

        return preview;
    }

    /// <summary><c>null</c> when the command names no versions. A file it does not name is taken at the version it has now.</summary>
    protected virtual async Task<IDictionary<string, string>> GetThemeFileETagsAsync(ContentRestoreCommand command, IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        if (command.ETags == null || command.ETags.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, string>();
        foreach (var path in paths)
        {
            result[path] = GetExpectedETag(command, new HistoryItem(HistoryItemType.Theme, path))
                ?? (await themeDraftService.ReadAsync(command.StoreId, path, cancellationToken)).ETag;
        }

        return result;
    }

    protected virtual string GetExpectedETag(ContentRestoreCommand command, HistoryItem item) =>
        command.ETags != null && command.ETags.TryGetValue(item, out var eTag) ? eTag : null;

    /// <summary>Sanitizes the content to save in the command, so that its versions and its history entry are of what is stored.</summary>
    /// <exception cref="InvalidDataException">Something in it can run script.</exception>
    protected virtual async Task GuardAsync(DesignerSaveCommand command, CancellationToken cancellationToken)
    {
        var catalog = await GetCatalogAsync(command.StoreId, command.CatalogHash, cancellationToken);
        var themeFiles = command.ThemeFiles.ToDictionary(x => x.Path, x => ParseOrNull(x.Content), StringComparer.OrdinalIgnoreCase);
        var customSections = catalog == null
            ? new Dictionary<string, IDictionary<string, JToken>>()
            : await GetCustomSectionsAsync(command.StoreId, themeFiles, cancellationToken);
        var pagePlacesCustomType = PlacesCustomType(command.PageContent) || ThemeCustomSections.PlacesCustomType(command.FilePage?.Content);
        var (pageCatalog, themeCatalogs) = await GetGuardCatalogsAsync(command.StoreId, catalog, pagePlacesCustomType, themeFiles, customSections,
            cancellationToken);
        var errors = new List<string>();

        command.PageContent = contentGuard.GuardPageContent(command.PageContent, PageName, pageCatalog, errors);
        if (command.FilePage?.Content != null)
        {
            errors.AddRange(contentGuard.GuardPage(command.FilePage.Content, command.FilePage.Path, pageCatalog).Errors);
        }

        foreach (var file in command.ThemeFiles)
        {
            file.Content = contentGuard.GuardThemeFileContent(SiteBuilderContentPaths.GetPathInTheme(file.Path), file.Content,
                themeCatalogs.TryGetValue(SiteBuilderContentPaths.GetThemeName(file.Path), out var themeCatalog) ? themeCatalog : catalog, errors);
        }

        SiteBuilderContentGuardExtensions.ThrowIfAny(errors);
    }

    /// <summary>A version restored is checked against the store's current catalog, as it is saved again.</summary>
    /// <exception cref="ThemeValidationException">A custom section type it restores breaks a rule, or a header or a footer places one.</exception>
    protected virtual async Task GuardAsync(ContentRestoreCommand command, CancellationToken cancellationToken)
    {
        var catalog = await ReadCatalogAsync(command.StoreId, hash: null, cancellationToken);
        var themeFiles = command.Contents
            .Where(x => x.Key.Type == HistoryItemType.Theme)
            .ToDictionary(x => x.Key.Key, x => ParseOrNull(x.Value), StringComparer.OrdinalIgnoreCase);
        var customSections = await GetCustomSectionsAsync(command.StoreId, themeFiles, cancellationToken);
        var pagePlacesCustomType = command.Contents.Any(x => x.Key.Type != HistoryItemType.Theme && PlacesCustomType(x.Value));
        var (pageCatalog, themeCatalogs) = await GetGuardCatalogsAsync(command.StoreId, catalog, pagePlacesCustomType, themeFiles, customSections,
            cancellationToken);
        var errors = new List<string>();

        foreach (var (item, content) in command.Contents.ToList())
        {
            command.Contents[item] = item.Type == HistoryItemType.Theme
                ? contentGuard.GuardThemeFileContent(SiteBuilderContentPaths.GetPathInTheme(item.Key), content,
                    themeCatalogs[SiteBuilderContentPaths.GetThemeName(item.Key)], errors)
                : contentGuard.GuardPageContent(content, PageName, pageCatalog, errors);
        }

        SiteBuilderContentGuardExtensions.ThrowIfAny(errors);

        var themeErrors = themeFiles.SelectMany(x => GetSectionGroupErrors(x.Key, x.Value))
            .Concat(ValidateCustomSections(themeFiles, customSections, catalog).Values.SelectMany(ThemeCustomSections.GetErrors))
            .ToList();
        if (themeErrors.Count > 0)
        {
            throw new ThemeValidationException(themeErrors);
        }
    }

    /// <summary>Null for no hash or an unknown one: the content is then checked without a schema.</summary>
    protected virtual async Task<JObject> GetCatalogAsync(string storeId, string catalogHash, CancellationToken cancellationToken) =>
        string.IsNullOrEmpty(catalogHash) ? null : await ReadCatalogAsync(storeId, catalogHash, cancellationToken);

    /// <summary>The store's current catalog for no hash.</summary>
    protected virtual async Task<JObject> ReadCatalogAsync(string storeId, string hash, CancellationToken cancellationToken)
    {
        var catalog = await schemaCatalogService.GetAsync(storeId, hash, cancellationToken);
        if (catalog == null)
        {
            return null;
        }

        try
        {
            return ThemeJson.ParseObject(catalog);
        }
        catch (JsonReaderException)
        {
            return null;
        }
    }

    /// <summary>Writes the page last, so that a refused page leaves the theme drafts to be put back.</summary>
    protected virtual async Task<string> SavePageAsync(DesignerSaveCommand command, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (command.Group != null)
        {
            var written = await groupedPageContentWriter.SaveContentAsync(command.Group, command.PageContent, command.PageETag, cancellationToken);
            return written.ETag;
        }

        if (command.FilePage == null)
        {
            return null;
        }

        if (command.FilePageVersion == null)
        {
            await filePageDraftService.SaveDraftAsync(command.StoreId, command.FilePage, user, cancellationToken);
        }
        else
        {
            await filePageDraftService.SaveDraftAsync(command.StoreId, command.FilePage, command.FilePageVersion, user, cancellationToken);
        }

        return null;
    }

    /// <summary>Discards the page's drafts last, so that a refused page leaves the theme drafts to be put back.</summary>
    protected virtual async Task<string> DiscardPageAsync(DesignerDiscardCommand command, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (command.Group != null)
        {
            return await DiscardPageDraftsAsync(command.Group, command.PageETag, cancellationToken);
        }

        if (command.FilePage == null)
        {
            return null;
        }

        var (contentType, path) = (command.FilePage.ContentType, command.FilePage.Path);
        if (command.PageETag != null)
        {
            await CheckFilePageDraftVersionAsync(command.StoreId, contentType, path, command.PageETag, user, cancellationToken);
        }

        if (await filePageDraftService.DiscardDraftAsync(command.StoreId, contentType, path, user, cancellationToken) == DraftDiscardStatus.OnlyCopy)
        {
            throw new InvalidDataException(NeverPublishedError);
        }

        return null;
    }

    /// <summary>The draft has to be there, at the version the caller reviewed. Neither blob storage nor git holds a lock over it: it is read right before the discard.</summary>
    /// <exception cref="SiteBuilderContentConflictException">It is not.</exception>
    protected virtual async Task CheckFilePageDraftVersionAsync(string storeId, string contentType, string path, string eTag, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var draft = await filePageDraftService.PreviewDiscardDraftAsync(storeId, contentType, path, user, cancellationToken);
        var current = draft.Status == DraftDiscardStatus.Discarded ? SiteBuilderContentVersion.CreateForFilePage(storeId, contentType, path, draft.DraftContent) : null;
        if (!string.Equals(current, eTag.Trim(), StringComparison.Ordinal))
        {
            throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.DraftChangedMessage);
        }
    }

    /// <param name="eTag">The version the draft has to be at; <c>null</c> discards it whatever it holds.</param>
    /// <exception cref="SiteBuilderContentConflictException">The draft is at another version: nothing is discarded.</exception>
    protected virtual async Task<string> DiscardPageDraftsAsync(GroupedSiteBuilderPage group, string eTag, CancellationToken cancellationToken)
    {
        var publishedPage = GetPublishedPage(group);

        var draftIds = group.Pages.Where(x => x.Status == Draft).Select(x => x.Id).ToList();
        if (eTag == null && draftIds.Count == 0)
        {
            return null;
        }

        // read first: the history entry needs it, and nothing may fail once the drafts are gone
        var publishedContent = await groupedPageService.LoadContent(publishedPage.Id, cancellationToken);
        if (eTag == null)
        {
            await pageService.DeleteAsync(draftIds);
        }
        else
        {
            // the version is compared, and the drafts deleted, under the lock a save of the page takes
            await groupedPageContentService.DeleteDraftsAtVersionAsync(group, eTag.Trim(), cancellationToken);
        }

        return publishedContent;
    }

    /// <exception cref="InvalidDataException">The page has never been published: its draft is the only copy.</exception>
    protected virtual SiteBuilderPage GetPublishedPage(GroupedSiteBuilderPage group) =>
        SiteBuilderPageSelection.Order(group.Pages).FirstOrDefault(x => x.Status == Published)
        ?? throw new InvalidDataException(NeverPublishedError);

    /// <param name="eTag">The version the draft has to be at; <c>null</c> takes the version it has now.</param>
    protected virtual async Task<bool> RestorePageAsync(GroupedSiteBuilderPage group, string content, string eTag, CancellationToken cancellationToken)
    {
        if (group == null)
        {
            return false;
        }

        eTag ??= (await groupedPageContentService.LoadGroupContentAsync(group, draft: true, cancellationToken)).ETag;
        await groupedPageContentWriter.SaveContentAsync(group, content, eTag, cancellationToken);

        return true;
    }

    protected virtual ThemeFileVersion CreateThemeFileVersion(string storeId, DesignerSaveFile file)
    {
        var version = AbstractTypeFactory<ThemeFileVersion>.TryCreateInstance();
        version.Path = file.Path;
        version.ETag = SiteBuilderContentVersion.CreateForThemeFile(storeId, file.Path, file.Content);

        return version;
    }

    protected virtual SiteBuilderObjectChange CreateContentChange(string objectType, string objectId, string eTag, string from, string to)
    {
        var field = AbstractTypeFactory<SiteBuilderFieldChange>.TryCreateInstance();
        field.Name = ModuleConstants.ChangePreview.Content;
        field.From = from;
        field.To = to;

        var change = AbstractTypeFactory<SiteBuilderObjectChange>.TryCreateInstance();
        change.ObjectType = objectType;
        change.ObjectId = objectId;
        change.ETag = eTag;
        change.Fields.Add(field);

        return change;
    }

    protected virtual string GetRestoredDescription(string versionId) =>
        $"{ModuleConstants.GitHistory.RestoredSummary} {versionId[..Math.Min(ModuleConstants.GitHistory.RestoredVersionLength, versionId.Length)]}";

    protected virtual ContentHistoryRecord CreateHistoryRecord(string description, string saveId, ClaimsPrincipal user) =>
        ContentHistoryRecord.Create(description, saveId, user);
}
