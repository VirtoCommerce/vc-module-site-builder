using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.ContentModule.Core;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.DistributedLock;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.StoreModule.Core.Services;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The services come from dependency injection; a parameter object would only hide them and change the constructor that subclasses call.")]
public class ThemePublisher(
    IThemeStorageFactory themeStorageFactory,
    IThemeSnapshotBuilder themeSnapshotBuilder,
    IThemeValidator themeValidator,
    IStoreService storeService,
    ISchemaCatalogService schemaCatalogService,
    IDistributedLock distributedLock,
    IEventPublisher eventPublisher,
    IContentHistory contentHistory,
    IOptions<ThemeSnapshotOptions> options,
    ILogger<ThemePublisher> logger,
    ISiteBuilderContentGuard contentGuard,
    IThemePaletteService themePaletteService) : IThemePublisher
{
    private const string PaletteTokensKey = "paletteTokens";

    private static readonly Dictionary<string, Action<ThemeSource, JToken>> ThemeParts = new(StringComparer.OrdinalIgnoreCase)
    {
        [ThemeStorage.TemplateRulesFile] = (source, content) => source.Rules = content,
        [ThemeStorage.HeaderGroupFile] = (source, content) => source.Header = content,
        [ThemeStorage.FooterGroupFile] = (source, content) => source.Footer = content,
        [ThemeStorage.BrandFile] = (source, content) => source.Brand = content,
        [ThemeStorage.SettingsDataFile] = (source, content) => source.SettingsData = content,
        [ThemeStorage.PaletteFile] = (source, content) => source.Palette = content,
        [ThemeStorage.DarkPaletteFile] = (source, content) => source.PaletteDark = content,
    };

    public virtual Task<int> PublishAsync(string storeId, string catalogHash, ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        return PublishAsync(storeId, catalogHash, expectedVersions: null, user, cancellationToken);
    }

    public virtual async Task<int> PublishAsync(string storeId, string catalogHash, IDictionary<string, string> expectedVersions, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var catalog = await GetCatalogAsync(storeId, catalogHash, cancellationToken);
        var result = await distributedLock.ExecuteAsync(
            ThemeStorage.GetLockResource(storeId),
            lockToken => PublishUnderLockAsync(storeId, catalog, catalogHash, includeDrafts: true, expectedVersions, lockToken, cancellationToken),
            cancellationToken: cancellationToken);

        await AfterPublishAsync(storeId, result.PublishedFiles, user, cancellationToken);

        return result.Publication;
    }

    public virtual async Task<IList<ThemeFileVersion>> PreviewPublishAsync(string storeId, string catalogHash, CancellationToken cancellationToken = default)
    {
        var catalog = await GetCatalogAsync(storeId, catalogHash, cancellationToken);
        _ = await storeService.GetNoCloneAsync(storeId) ?? throw new KeyNotFoundException($"Store '{storeId}' not found.");
        var storage = await themeStorageFactory.OpenAsync(storeId);

        // read without the theme lock: nothing is written, and the publication checks the versions again under it
        var source = await GetThemeSourceAsync(storage, includeDrafts: true);
        var versions = GetDraftVersions(storeId, storage, source);
        var errors = ValidateThemeSource(source, catalog, includeDrafts: true);
        if (errors.Count > 0)
        {
            throw new ThemeValidationException([.. errors]);
        }

        return versions;
    }

    public virtual Task<int> PublishFirstAsync(string storeId, CancellationToken cancellationToken = default)
    {
        return distributedLock.ExecuteAsync(
            ThemeStorage.GetLockResource(storeId),
            async lockToken =>
            {
                var storage = await themeStorageFactory.OpenAsync(storeId);
                if (await storage.GetCurrentPublicationAsync() is { } publication)
                {
                    return publication;
                }

                // Live files may predate the storefront schemas, so only their shape is validated.
                var currentCatalog = await schemaCatalogService.GetAsync(storeId, hash: null, lockToken);
                var catalog = currentCatalog == null ? null : ThemeJson.ParseObject(currentCatalog);
                var result = await PublishUnderLockAsync(storeId, catalog, catalogHash: null, includeDrafts: false, expectedVersions: null, lockToken, cancellationToken);

                return result.Publication;
            },
            cancellationToken: cancellationToken);
    }

    public virtual async Task<ThemeStatus> GetStatusAsync(string storeId, CancellationToken cancellationToken = default)
    {
        var storage = await themeStorageFactory.OpenAsync(storeId);

        var result = AbstractTypeFactory<ThemeStatus>.TryCreateInstance();
        result.Publication = await storage.GetCurrentPublicationAsync();
        result.Drafts = await GetDraftsAsync(storage);
        result.NewDrafts = await GetNewDraftsAsync(storage, result.Drafts);

        return result;
    }

    public virtual async Task ValidateFilesAsync(string storeId, string catalogHash, IDictionary<string, JToken> files, CancellationToken cancellationToken = default)
    {
        var catalog = await GetCatalogAsync(storeId, catalogHash, cancellationToken);

        var source = AbstractTypeFactory<ThemeSource>.TryCreateInstance();
        foreach (var (file, content) in files)
        {
            AddThemeFile(source, file, content);
        }

        var errors = themeValidator.Validate(source, catalog);
        if (errors.Count > 0)
        {
            throw new ThemeValidationException([.. errors]);
        }
    }

    /// <param name="cancellationToken">Cancelled when the theme lock is lost: another server may hold it, so nothing more is written.</param>
    /// <param name="requestToken">The request's own token: cancelled with it the lock is still held, so the live files go back.</param>
    /// <param name="expectedVersions">The version of every draft; <c>null</c> publishes the drafts whatever they hold.</param>
    protected virtual async Task<ThemePublishResult> PublishUnderLockAsync(string storeId, JObject catalog, string catalogHash, bool includeDrafts,
        IDictionary<string, string> expectedVersions, CancellationToken cancellationToken, CancellationToken requestToken)
    {
        var store = await storeService.GetNoCloneAsync(storeId) ?? throw new KeyNotFoundException($"Store '{storeId}' not found.");
        var storage = await themeStorageFactory.OpenAsync(storeId);

        var source = await GetThemeSourceAsync(storage, includeDrafts);
        // before the drafts are sanitized: a version is of the draft as it is stored
        CheckDraftVersions(expectedVersions, GetDraftVersions(storeId, storage, source));
        var validationCatalog = catalogHash == null ? null : catalog;
        var errors = ValidateThemeSource(source, validationCatalog, includeDrafts);
        if (errors.Count > 0)
        {
            throw new ThemeValidationException([.. errors]);
        }

        var languages = store.Languages?.ToList() ?? [];
        var defaultLanguage = store.GetThemeDefaultLanguage();
        var publication = await storage.GetNextPublicationAsync();
        var snapshots = themeSnapshotBuilder.Build(source, catalog, catalogHash, languages, defaultLanguage, publication, DateTime.UtcNow);

        var record = AbstractTypeFactory<ThemePublicationRecord>.TryCreateInstance();
        record.DefaultLanguage = defaultLanguage;

        // the pointer moves last: a failure before it leaves the previous publication current and every draft unpublished
        var replaced = new List<LiveFile>();
        try
        {
            foreach (var (language, content) in snapshots.Files)
            {
                await storage.WriteSnapshotAsync(publication, language, content);
                LogSnapshotSize(storeId, publication, language, content);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await WriteLiveFilesAsync(storage, source.Drafts, replaced, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await storage.WritePublicationRecordAsync(publication, record);
            cancellationToken.ThrowIfCancellationRequested();
            await storage.SetCurrentPublicationAsync(publication);
        }
        catch
        {
            // with the lock lost the live files may be another server's by now; the drafts are intact, and the next publication puts them live
            if (!ThemeStorage.IsLockLost(cancellationToken, requestToken))
            {
                await RestoreLiveFilesAsync(storeId, storage, replaced);
                await RemovePublicationAsync(storeId, storage, publication);
            }

            throw;
        }

        ThemeSnapshotCacheRegion.ExpireTokenForKey(storeId, propagate: true);
        await RemoveDraftsAsync(storeId, storage, source.Drafts.Keys);

        var result = AbstractTypeFactory<ThemePublishResult>.TryCreateInstance();
        result.Publication = publication;
        result.PublishedFiles = GetPublishedFiles(storage, source.Drafts);

        return result;
    }

    /// <summary>Also sanitizes the HTML of the files, see <see cref="GuardThemeSource"/>.</summary>
    protected virtual IList<string> ValidateThemeSource(ThemeSource source, JObject catalog, bool includeDrafts)
    {
        var errors = GuardThemeSource(source, catalog).Concat(themeValidator.Validate(source, catalog));

        // a publication of the drafts is what an editor asks for; a first publication puts the live files out as they are
        return (includeDrafts ? errors.Concat(ValidatePalette(source, catalog)) : errors).ToList();
    }

    /// <summary>
    /// The palette as the storefront gets it must meet WCAG 2.2 AA in both color schemes. The pairs are the designer's palette check,
    /// with a token the storefront announced and the palette leaves out not checked, as its color comes from the storefront's preset.
    /// </summary>
    protected virtual IList<string> ValidatePalette(ThemeSource source, JObject catalog)
    {
        // without light colors the snapshot has no palette at all, and the storefront keeps its preset
        if (ThemePaletteValues.ReadColors(source.Palette) is not { } light)
        {
            return [];
        }

        var palette = AbstractTypeFactory<ThemePalette>.TryCreateInstance();
        palette.Light = light;
        palette.Dark = ThemePaletteValues.ReadColors(source.PaletteDark);

        var request = AbstractTypeFactory<ThemePaletteCheckRequest>.TryCreateInstance();
        request.Palette = palette;
        request.Tokens = GetPaletteTokens(catalog);

        return themePaletteService.CheckPalette(request).Failures.Select(GetContrastError).ToList();
    }

    protected virtual ThemePaletteTokens GetPaletteTokens(JObject catalog)
    {
        var tokens = AbstractTypeFactory<ThemePaletteTokens>.TryCreateInstance();
        if (catalog?[PaletteTokensKey] is JObject announced)
        {
            tokens.Light = GetStrings(announced[ModuleConstants.Palette.Light]);
            tokens.Dark = GetStrings(announced[ModuleConstants.Palette.Dark]);
        }

        return tokens;
    }

    protected virtual string GetContrastError(PaletteContrastFailure failure)
    {
        var file = failure.Scheme == ModuleConstants.Palette.Dark ? ThemeStorage.DarkPaletteFile : ThemeStorage.PaletteFile;

        return string.Create(CultureInfo.InvariantCulture,
            $"{file}: '{failure.Pair}' has a contrast of {failure.Ratio:0.##}:1 ({failure.Text} {failure.TextColor} on {failure.Background} {failure.BackgroundColor}), less than the {failure.MinimumRatio:0.##}:1 WCAG 2.2 AA asks for");
    }

    /// <summary>Each draft by its path with the theme folder, at the version the designer reads it at.</summary>
    protected virtual IList<ThemeFileVersion> GetDraftVersions(string storeId, IThemeStorage storage, ThemeSource source)
    {
        return source.Drafts
            .Select(x =>
            {
                var version = AbstractTypeFactory<ThemeFileVersion>.TryCreateInstance();
                version.Path = storage.GetPath(x.Key);
                version.ETag = SiteBuilderContentVersion.CreateForThemeFile(storeId, version.Path, x.Value);

                return version;
            })
            .ToList();
    }

    /// <summary>The drafts must be those reviewed: none changed, none added and none gone, as each changes what goes live.</summary>
    /// <exception cref="SiteBuilderContentConflictException">They are not.</exception>
    protected virtual void CheckDraftVersions(IDictionary<string, string> expectedVersions, IList<ThemeFileVersion> drafts)
    {
        if (expectedVersions == null)
        {
            return;
        }

        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, eTag) in expectedVersions)
        {
            expected[path?.TrimStart('/') ?? string.Empty] = eTag?.Trim();
        }

        if (expected.Count != drafts.Count ||
            drafts.Any(x => !expected.TryGetValue(x.Path, out var eTag) || !string.Equals(eTag, x.ETag, StringComparison.Ordinal)))
        {
            throw new SiteBuilderContentConflictException(SiteBuilderContentConflictException.ThemeDraftsMessage);
        }
    }

    private static List<string> GetStrings(JToken token) =>
        (token as JArray)?.OfType<JValue>().Where(x => x.Type == JTokenType.String).Select(x => (string)x).ToList() ?? [];

    /// <summary>
    /// Sanitizes the HTML of every file again, as a draft saved before the checks existed may hold some that can run script:
    /// the snapshot is built from the sanitized files, and a sanitized draft goes live as sanitized.
    /// </summary>
    protected virtual IList<string> GuardThemeSource(ThemeSource source, JObject catalog)
    {
        var files = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase)
        {
            [ThemeStorage.TemplateRulesFile] = source.Rules,
            [ThemeStorage.HeaderGroupFile] = source.Header,
            [ThemeStorage.FooterGroupFile] = source.Footer,
            [ThemeStorage.BrandFile] = source.Brand,
            [ThemeStorage.SettingsDataFile] = source.SettingsData,
            [ThemeStorage.PaletteFile] = source.Palette,
            [ThemeStorage.DarkPaletteFile] = source.PaletteDark,
        };
        foreach (var (key, document) in source.Templates)
        {
            files[ThemeStorage.GetTemplateFile(key)] = document;
        }

        var errors = new List<string>();
        foreach (var (file, content) in files.Where(x => x.Value != null))
        {
            var result = contentGuard.GuardThemeFile(file, content, catalog);
            errors.AddRange(result.Errors);

            if (result.Changed && source.Drafts.TryGetValue(file, out var draft))
            {
                source.Drafts[file] = SiteBuilderContentGuardExtensions.Serialize(content, draft);
            }
        }

        return errors;
    }

    protected virtual async Task<JObject> GetCatalogAsync(string storeId, string catalogHash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(catalogHash))
        {
            throw new ThemeValidationException(["the publication names no schema catalog: preview the storefront first"]);
        }

        var catalog = await schemaCatalogService.GetAsync(storeId, catalogHash, cancellationToken)
            ?? throw new ThemeValidationException([$"there is no schema catalog '{catalogHash}': preview the storefront first"]);

        return ThemeJson.ParseObject(catalog);
    }

    protected virtual async Task<ThemeSource> GetThemeSourceAsync(IThemeStorage storage, bool includeDrafts)
    {
        var source = AbstractTypeFactory<ThemeSource>.TryCreateInstance();
        source.ThemeName = storage.ThemeName;

        foreach (var file in await GetThemeFilesAsync(storage))
        {
            AddThemeFile(source, file, await ReadThemeFileAsync(storage, source, file, includeDrafts));
        }

        return source;
    }

    protected virtual async Task<IList<string>> GetThemeFilesAsync(IThemeStorage storage)
    {
        var templateKeys = await GetTemplateKeysAsync(storage);

        return ThemeParts.Keys.Concat(templateKeys.Select(ThemeStorage.GetTemplateFile)).ToList();
    }

    protected virtual void AddThemeFile(ThemeSource source, string file, JToken content)
    {
        if (ThemeParts.TryGetValue(file, out var setPart))
        {
            setPart(source, content);
        }
        else if (content != null && GetTemplateKey(file) is { } key)
        {
            source.Templates[key] = content;
        }
    }

    protected virtual string GetTemplateKey(string file)
    {
        var folder = ThemeStorage.TemplatesFolder + "/";

        return file.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? ThemeStorage.GetTemplateKey(file[folder.Length..]) : null;
    }

    protected virtual async Task<IList<string>> GetDraftsAsync(IThemeStorage storage)
    {
        var drafts = new List<string>();
        foreach (var file in await GetThemeFilesAsync(storage))
        {
            if (await storage.ExistsAsync(storage.GetDraftPath(file)))
            {
                drafts.Add(file);
            }
        }

        return drafts;
    }

    protected virtual async Task<IList<string>> GetNewDraftsAsync(IThemeStorage storage, IEnumerable<string> drafts)
    {
        var newDrafts = new List<string>();
        foreach (var file in drafts)
        {
            if (!await storage.ExistsAsync(file))
            {
                newDrafts.Add(file);
            }
        }

        return newDrafts;
    }

    protected virtual async Task<JToken> ReadThemeFileAsync(IThemeStorage storage, ThemeSource source, string file, bool includeDrafts)
    {
        var draft = includeDrafts ? await storage.ReadAsync(storage.GetDraftPath(file)) : null;
        if (draft != null)
        {
            source.Drafts[file] = draft;
        }

        var content = draft ?? await storage.ReadAsync(file);
        if (content == null)
        {
            return null;
        }

        try
        {
            return ThemeJson.Parse(content);
        }
        catch (JsonReaderException)
        {
            source.InvalidFiles.Add(file);
            return null;
        }
    }

    protected virtual async Task<IList<string>> GetTemplateKeysAsync(IThemeStorage storage)
    {
        var fileNames = await storage.ListFilesAsync(ThemeStorage.TemplatesFolder);

        return fileNames
            .Select(x => ThemeStorage.GetTemplateKey(storage.GetLivePath(x)))
            .Where(x => x != null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Adds each live file to <paramref name="replaced"/> before writing over it, so a failure can put it back.</summary>
    protected virtual async Task WriteLiveFilesAsync(IThemeStorage storage, IDictionary<string, string> drafts, IList<LiveFile> replaced, CancellationToken cancellationToken)
    {
        foreach (var (file, content) in drafts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            replaced.Add(new LiveFile(file, await storage.ReadAsync(file)));
            await storage.WriteAsync(file, content);
        }
    }

    protected virtual async Task RestoreLiveFilesAsync(string storeId, IThemeStorage storage, IList<LiveFile> files)
    {
        foreach (var file in files.Reverse())
        {
            try
            {
                if (file.Content == null)
                {
                    await storage.RemoveAsync([file.File]);
                }
                else
                {
                    await storage.WriteAsync(file.File, file.Content);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not put back the live file {File} of theme {Theme} (store {StoreId}) after a failed publication.",
                    file.File, storage.ThemeName, storeId);
            }
        }
    }

    /// <summary>A failed publication left with its record would be served wherever the pointer cannot be read.</summary>
    protected virtual async Task RemovePublicationAsync(string storeId, IThemeStorage storage, int publication)
    {
        try
        {
            await storage.RemovePublicationAsync(publication);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not remove the failed publication {Publication} of theme {Theme} (store {StoreId}).",
                publication, storage.ThemeName, storeId);
        }
    }

    /// <summary>A draft left behind holds what is live, so a failure does not fail the publication.</summary>
    protected virtual async Task RemoveDraftsAsync(string storeId, IThemeStorage storage, ICollection<string> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        try
        {
            await storage.RemoveAsync(files.Select(storage.GetDraftPath));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove the published drafts of theme {Theme} (store {StoreId}); they hold what is live.",
                storage.ThemeName, storeId);
        }
    }

    protected virtual IList<PublishedThemeFile> GetPublishedFiles(IThemeStorage storage, IDictionary<string, string> drafts)
    {
        return drafts
            .Select(x =>
            {
                var file = AbstractTypeFactory<PublishedThemeFile>.TryCreateInstance();
                file.Path = storage.GetPath(x.Key);
                file.Content = x.Value;

                return file;
            })
            .ToList();
    }

    protected virtual async Task AfterPublishAsync(string storeId, IList<PublishedThemeFile> files, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return;
        }

        var changedEntries = files.Select(x => CreateChangedEntry(x.Path, EntryState.Modified)).ToList();
        await eventPublisher.Publish(new SiteBuilderContentChangedEvent(ContentConstants.ContentTypes.Themes, changedEntries), cancellationToken);

        var record = ContentHistoryRecord.Create(ModuleConstants.GitHistory.PublishedSummary, Guid.NewGuid().ToString("N"), user);
        foreach (var file in files)
        {
            record.AddEntry(HistoryItem.ForThemeFile(file.Path), file.Content);
        }

        await contentHistory.RecordAsync(storeId, record, cancellationToken);
    }

    protected virtual GenericChangedEntry<FileEntity> CreateChangedEntry(string path, EntryState state)
    {
        var file = AbstractTypeFactory<FileEntity>.TryCreateInstance();
        file.Path = path;
        file.Type = ContentConstants.ContentTypes.Themes;

        return new GenericChangedEntry<FileEntity>(file, state);
    }

    protected virtual void LogSnapshotSize(string storeId, int publication, string language, string content)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            gzip.Write(bytes, 0, bytes.Length);
        }

        var threshold = options.Value.SizeWarningThreshold;
        if (compressed.Length > threshold)
        {
            logger.LogWarning(
                "The theme snapshot {Publication} of store {StoreId} in {Language} is {Size} bytes gzipped, more than {Limit}.",
                publication, storeId, language, compressed.Length, threshold);
        }
    }

    protected sealed record LiveFile(string File, string Content);
}
