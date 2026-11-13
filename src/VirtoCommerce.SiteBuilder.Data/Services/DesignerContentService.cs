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
    IThemeSnapshotService themeSnapshotService,
    ISiteBuilderPageService pageService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService groupedPageContentService,
    IGroupedPageContentWriter groupedPageContentWriter,
    IFilePageDraftService filePageDraftService,
    IContentHistory contentHistory,
    ISiteBuilderContentGuard contentGuard,
    ISchemaCatalogService schemaCatalogService) : IDesignerContentService
{
    private const string NeverPublishedError = "This page has never been published, so there is no published version to go back to.";
    private const string PageName = "page";

    public virtual Task ValidateThemeFilesAsync(string storeId, IDictionary<string, JToken> files, bool draft, string catalogHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        return ValidateThemeFilesInternalAsync(storeId, files, draft, catalogHash, cancellationToken);
    }

    private async Task ValidateThemeFilesInternalAsync(string storeId, IDictionary<string, JToken> files, bool draft, string catalogHash,
        CancellationToken cancellationToken)
    {
        // the new Site Builder publishes the theme as a whole, from drafts
        if (!draft && await themeSnapshotService.IsNewSiteBuilderEnabledAsync(storeId, cancellationToken))
        {
            throw new InvalidDataException("The store uses the new Site Builder: theme files are saved as drafts and published with theme/publish.");
        }

        var catalog = await GetCatalogAsync(storeId, catalogHash, cancellationToken);
        var errors = files
            .SelectMany(x => contentGuard.GuardThemeFile(SiteBuilderContentPaths.GetPathInTheme(x.Key), x.Value, catalog).Errors)
            .ToList();
        if (errors.Count > 0)
        {
            throw new ThemeValidationException(errors);
        }

        if (string.IsNullOrEmpty(catalogHash))
        {
            return;
        }

        // each theme folder on its own: two themes may hold a file of the same name
        foreach (var theme in files.GroupBy(x => SiteBuilderContentPaths.GetThemeName(x.Key), StringComparer.OrdinalIgnoreCase))
        {
            var contents = theme.ToDictionary(x => SiteBuilderContentPaths.GetPathInTheme(x.Key), x => x.Value, StringComparer.OrdinalIgnoreCase);
            await themePublisher.ValidateFilesAsync(storeId, catalogHash, contents, cancellationToken);
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
            preview.Objects.Add(CreateContentChange(ModuleConstants.ChangePreview.FilePage, command.FilePage.Path, eTag: null, current,
                PageJson.Serialize(command.FilePage.Content)));
        }

        foreach (var file in command.ThemeFiles)
        {
            var current = await themeDraftService.ReadAsync(command.StoreId, file.Path, cancellationToken);
            preview.Objects.Add(CreateContentChange(ModuleConstants.ChangePreview.ThemeFile, file.Path, current.ETag, current.Content, file.Content));
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
        // a version restored replaces the drafts as they are now, as the page's restore does
        await themeDraftService.SaveDraftsAsync(command.StoreId, themeFiles, eTags: null, token => RestorePageAsync(command.Group, pageContent, token), cancellationToken);

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

    /// <summary>Sanitizes the content to save in the command, so that its versions and its history entry are of what is stored.</summary>
    /// <exception cref="InvalidDataException">Something in it can run script.</exception>
    protected virtual async Task GuardAsync(DesignerSaveCommand command, CancellationToken cancellationToken)
    {
        var catalog = await GetCatalogAsync(command.StoreId, command.CatalogHash, cancellationToken);
        var errors = new List<string>();

        command.PageContent = contentGuard.GuardPageContent(command.PageContent, PageName, catalog, errors);
        if (command.FilePage?.Content != null)
        {
            errors.AddRange(contentGuard.GuardPage(command.FilePage.Content, command.FilePage.Path, catalog).Errors);
        }

        foreach (var file in command.ThemeFiles)
        {
            file.Content = contentGuard.GuardThemeFileContent(SiteBuilderContentPaths.GetPathInTheme(file.Path), file.Content, catalog, errors);
        }

        SiteBuilderContentGuardExtensions.ThrowIfAny(errors);
    }

    /// <summary>A version restored is checked against the store's current catalog, as it is saved again.</summary>
    protected virtual async Task GuardAsync(ContentRestoreCommand command, CancellationToken cancellationToken)
    {
        var catalog = await ReadCatalogAsync(command.StoreId, hash: null, cancellationToken);
        var errors = new List<string>();

        foreach (var (item, content) in command.Contents.ToList())
        {
            command.Contents[item] = item.Type == HistoryItemType.Theme
                ? contentGuard.GuardThemeFileContent(SiteBuilderContentPaths.GetPathInTheme(item.Key), content, catalog, errors)
                : contentGuard.GuardPageContent(content, PageName, catalog, errors);
        }

        SiteBuilderContentGuardExtensions.ThrowIfAny(errors);
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

        if (command.FilePage != null)
        {
            await filePageDraftService.SaveDraftAsync(command.StoreId, command.FilePage, user, cancellationToken);
        }

        return null;
    }

    /// <summary>Discards the page's drafts last, so that a refused page leaves the theme drafts to be put back.</summary>
    protected virtual async Task<string> DiscardPageAsync(DesignerDiscardCommand command, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (command.Group != null)
        {
            return await DiscardPageDraftsAsync(command.Group, cancellationToken);
        }

        if (command.FilePage != null &&
            await filePageDraftService.DiscardDraftAsync(command.StoreId, command.FilePage.ContentType, command.FilePage.Path, user, cancellationToken) == DraftDiscardStatus.OnlyCopy)
        {
            throw new InvalidDataException(NeverPublishedError);
        }

        return null;
    }

    protected virtual async Task<string> DiscardPageDraftsAsync(GroupedSiteBuilderPage group, CancellationToken cancellationToken)
    {
        var publishedPage = GetPublishedPage(group);

        var draftIds = group.Pages.Where(x => x.Status == Draft).Select(x => x.Id).ToList();
        if (draftIds.Count == 0)
        {
            return null;
        }

        // read first: the history entry needs it, and nothing may fail once the drafts are gone
        var publishedContent = await groupedPageService.LoadContent(publishedPage.Id, cancellationToken);
        await pageService.DeleteAsync(draftIds);

        return publishedContent;
    }

    /// <exception cref="InvalidDataException">The page has never been published: its draft is the only copy.</exception>
    protected virtual SiteBuilderPage GetPublishedPage(GroupedSiteBuilderPage group) =>
        SiteBuilderPageSelection.Order(group.Pages).FirstOrDefault(x => x.Status == Published)
        ?? throw new InvalidDataException(NeverPublishedError);

    protected virtual async Task<bool> RestorePageAsync(GroupedSiteBuilderPage group, string content, CancellationToken cancellationToken)
    {
        if (group == null)
        {
            return false;
        }

        var current = await groupedPageContentService.LoadGroupContentAsync(group, draft: true, cancellationToken);
        await groupedPageContentWriter.SaveContentAsync(group, content, current.ETag, cancellationToken);

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
