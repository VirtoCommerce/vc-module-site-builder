using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.AssetsModule.Core.Assets;
using VirtoCommerce.ContentModule.Core.Services;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.SiteBuilder.Data.GitContent;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.Platform.Core.Common;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.SchemaKinds;

namespace VirtoCommerce.SiteBuilder.Data.Services;

[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The services come from dependency injection; a parameter object would only hide them and change the constructor that subclasses call.")]
public class SiteBuilderContentQueryService(
    IAuthorizationService authorizationService,
    IValidator<SiteBuilderContentRequest> requestValidator,
    IContentPathResolver pathResolver,
    IBlobContentStorageProviderFactory blobContentStorageProviderFactory,
    IPublishingService publishingService,
    IThemeStorageFactory themeStorageFactory,
    IGitContentPolicy gitContentPolicy,
    IGitContentRepository gitContentRepository,
    IGitHubConnectionService gitHubConnectionService,
    IOptions<GitContentOptions> gitContentOptions,
    IGitContentHistory gitContentHistory) : ISiteBuilderContentQueryService
{
    private const string Themes = "themes";
    private const string PagesContentType = "pages";
    private const string JsonExtension = ".json";
    private const string FolderEntryType = "folder";

    public virtual Task<SiteBuilderOperationResult<string>> GetSchemasAsync(SiteBuilderSchemaQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetSchemasInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<string>> GetSchemasInternalAsync(SiteBuilderSchemaQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (await CheckRequestAsync(ThemeRequest(request), request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<string>();
        }

        return SiteBuilderOperationResult.Success(await GetSchemaFilesAsync(request.StoreId, request.Theme, request.Kind));
    }

    public virtual Task<SiteBuilderOperationResult<string>> GetSectionSchemasAsync(SiteBuilderThemeQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetSectionSchemasInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<string>> GetSectionSchemasInternalAsync(SiteBuilderThemeQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (await CheckRequestAsync(ThemeRequest(request), request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<string>();
        }

        var sections = await GetSchemaFilesAsync(request.StoreId, request.Theme, Sections);
        var blocks = await GetSchemaFilesAsync(request.StoreId, request.Theme, Blocks);
        var objects = await GetSchemaFilesAsync(request.StoreId, request.Theme, Objects);
        var shared = await GetSchemaFilesAsync(request.StoreId, request.Theme, Shared);

        return SiteBuilderOperationResult.Success($"{{ \"{Sections}\": {sections}, \"{Blocks}\": {blocks}, \"{Objects}\": {objects}, \"{Shared}\": {shared} }}");
    }

    public virtual Task<SiteBuilderOperationResult<string>> GetSchemaCatalogAsync(SiteBuilderThemeQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetSchemaCatalogInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<string>> GetSchemaCatalogInternalAsync(SiteBuilderThemeQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (await CheckRequestAsync(ThemeRequest(request), request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<string>();
        }

        var catalog = new JObject
        {
            [Sections] = await GetSchemaCatalogAsync(request.StoreId, request.Theme, Sections, filterInternal: true),
            [Templates] = await GetSchemaCatalogAsync(request.StoreId, request.Theme, Templates, filterInternal: true),
            [Blocks] = await GetSchemaCatalogAsync(request.StoreId, request.Theme, Blocks, filterInternal: true),
            [Objects] = await GetSchemaCatalogAsync(request.StoreId, request.Theme, Objects, filterInternal: true),
            [Shared] = await GetSchemaCatalogAsync(request.StoreId, request.Theme, Shared, filterInternal: false),
        };

        return SiteBuilderOperationResult.Success(catalog.ToString(Formatting.None));
    }

    public virtual Task<SiteBuilderOperationResult<string>> GetSchemaAsync(SiteBuilderSchemaQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetSchemaInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<string>> GetSchemaInternalAsync(SiteBuilderSchemaQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var contentRequest = ThemeRequest(request);
        contentRequest.SchemaKind = request.Kind;
        contentRequest.SchemaKey = request.Key;
        if (await CheckRequestAsync(contentRequest, request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<string>();
        }

        var kind = request.Kind;
        var key = request.Key;

        // Underscore-prefixed keys are theme-internal; only `shared` exposes them (e.g. _sections, _blocks).
        if (kind != Shared && key.StartsWith('_'))
        {
            return SiteBuilderOperationResult.NotFound().As<string>();
        }

        var themeName = await GetCurrentThemeName(request.StoreId, request.Theme);
        var schemasFolder = $"{themeName}/config/schemas/{kind}";
        var basePath = GetContentBasePath(request.StoreId, Themes, themeName);
        var storageProvider = blobContentStorageProviderFactory.CreateProvider(basePath);
        var allFiles = await storageProvider.SearchAsync(schemasFolder, null);
        var file = allFiles.Results.FirstOrDefault(x =>
            x.Type != FolderEntryType &&
            x.Name.EndsWith(JsonExtension, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileNameWithoutExtension(x.Name).Equals(key, StringComparison.OrdinalIgnoreCase));

        if (file == null)
        {
            return SiteBuilderOperationResult.NotFound().As<string>();
        }

        var content = GetContent(file, storageProvider);

        if (kind == Templates)
        {
            content = await MergeStaticSectionsIntoTemplateAsync(content, request.StoreId, themeName);
        }

        return SiteBuilderOperationResult.Success(content);
    }

    public virtual Task<SiteBuilderOperationResult<SiteBuilderFileContent>> GetBuilderSettingsAsync(SiteBuilderThemeQueryRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetBuilderSettingsInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<SiteBuilderFileContent>> GetBuilderSettingsInternalAsync(SiteBuilderThemeQueryRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await CheckRequestAsync(ThemeRequest(request), request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<SiteBuilderFileContent>();
        }

        var themeName = await GetCurrentThemeName(request.StoreId, request.Theme);
        var filePath = $"{themeName}/config/builder_settings.json";
        var basePath = GetContentBasePath(request.StoreId, Themes, themeName);
        var storageProvider = blobContentStorageProviderFactory.CreateProvider(basePath);

        var blobInfo = await storageProvider.GetBlobInfoAsync(filePath);
        var gitFlow = await gitContentPolicy.IsEnabledForStoreAsync(request.StoreId, cancellationToken);
        var result = AbstractTypeFactory<SiteBuilderFileContent>.TryCreateInstance();

        if (blobInfo == null)
        {
            result.Text = gitFlow ? GitBuilderSettings.GetDescriptors().ToString(Formatting.None) : "{}";
            return SiteBuilderOperationResult.Success(result);
        }

        if (!gitFlow)
        {
            result.Stream = await storageProvider.OpenReadAsync(blobInfo.RelativeUrl);
            result.Name = blobInfo.Name;
            return SiteBuilderOperationResult.Success(result);
        }

        // The builder overlays this response on top of its bundled data/settings.json, server wins.
        // That is how a store on the git flow gets git urls for publishing without anyone editing the
        // app's static config — and how a store that is not on it keeps the bytes it has always got.
        var settings = await ReadJsonBlobAsync(storageProvider, blobInfo.RelativeUrl);
        settings.Merge(GitBuilderSettings.GetDescriptors(), new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
        result.Text = settings.ToString(Formatting.None);

        return SiteBuilderOperationResult.Success(result);
    }

    public virtual Task<SiteBuilderOperationResult<SiteBuilderFileContent>> ReadFileAsync(SiteBuilderFileReadRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ReadFileInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<SiteBuilderFileContent>> ReadFileInternalAsync(SiteBuilderFileReadRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var contentRequest = new SiteBuilderContentRequest
        {
            StoreId = request.StoreId,
            ContentType = request.ContentType,
            Theme = request.Theme,
            Paths = string.IsNullOrEmpty(request.Path) ? [] : [request.Path],
            Ref = request.Ref,
        };
        if (await CheckRequestAsync(contentRequest, request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<SiteBuilderFileContent>();
        }

        // With the git flow on, a page is read from git: the caller's URL does not change, because
        // resolving "this editor's draft, else what is published" is the server's job.
        var repository = GitPageLocation.IsPageContent(request.ContentType) && !string.IsNullOrEmpty(request.Path)
            ? await gitContentPolicy.GetRepositoryAsync(request.StoreId, cancellationToken)
            : null;
        if (repository != null)
        {
            var fromGit = await ReadPageFromGitAsync(repository, request, user, cancellationToken);
            if (fromGit != null)
            {
                var result = AbstractTypeFactory<SiteBuilderFileContent>.TryCreateInstance();
                result.Text = fromGit;
                return SiteBuilderOperationResult.Success(result);
            }

            if (!string.IsNullOrEmpty(request.Ref))
            {
                // an exact commit was asked for; blob storage cannot answer that question
                var notFound = AbstractTypeFactory<SiteBuilderFileContent>.TryCreateInstance();
                notFound.Ref = request.Ref;
                return SiteBuilderOperationResult.NotFound(notFound);
            }

            // Not in the repository — fall through to blob storage. A store that opts in keeps every
            // page it had: those only reach git at their first save from the designer, and until then
            // the blob copy is the page. Refusing to read it would make opting in look like the
            // builder lost the store's content.
        }

        return await ReadFileFromBlobAsync(request);
    }

    public virtual Task<SiteBuilderOperationResult<IList<ThemeTemplate>>> ListTemplatesAsync(SiteBuilderThemeQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ListTemplatesInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<IList<ThemeTemplate>>> ListTemplatesInternalAsync(SiteBuilderThemeQueryRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (await CheckRequestAsync(ThemeRequest(request), request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<IList<ThemeTemplate>>();
        }

        var storage = themeStorageFactory.Open(request.StoreId, await GetCurrentThemeName(request.StoreId, request.Theme));
        var templates = new SortedDictionary<string, ThemeTemplate>(StringComparer.Ordinal);
        foreach (var fileName in await storage.ListFilesAsync(ThemeStorage.TemplatesFolder))
        {
            var liveName = storage.GetLivePath(fileName);
            if (ThemeStorage.GetTemplateKey(liveName) is not { } key)
            {
                continue;
            }

            if (!templates.TryGetValue(key, out var template))
            {
                template = CreateTemplate(key);
                templates[key] = template;
            }

            if (string.Equals(liveName, fileName, StringComparison.Ordinal))
            {
                template.Published = true;
            }
            else
            {
                template.HasDraft = true;
            }
        }

        return SiteBuilderOperationResult.Success<IList<ThemeTemplate>>([.. templates.Values]);
    }

    public virtual Task<SiteBuilderOperationResult<IList<SiteBuilderFileSearchItem>>> SearchFilesAsync(SiteBuilderFileSearchRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SearchFilesInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<IList<SiteBuilderFileSearchItem>>> SearchFilesInternalAsync(SiteBuilderFileSearchRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var contentRequest = new SiteBuilderContentRequest
        {
            StoreId = request.StoreId,
            ContentType = request.ContentType,
            Theme = request.Theme,
            Paths = string.IsNullOrEmpty(request.Folder) ? [] : [request.Folder],
            Keyword = request.Keyword,
        };
        if (await CheckRequestAsync(contentRequest, request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<IList<SiteBuilderFileSearchItem>>();
        }

        var storageProvider = blobContentStorageProviderFactory.CreateProvider(GetContentBasePath(request.StoreId, request.ContentType, request.Theme));
        var files = await FindFilesAsync(storageProvider, request.Folder, request.Keyword, request.Pattern);

        return SiteBuilderOperationResult.Success(ReadSearchItems(files, request.ContentType, storageProvider));
    }

    private IList<SiteBuilderFileSearchItem> ReadSearchItems(IEnumerable<BlobEntry> files, string contentType, IBlobContentStorageProvider storageProvider)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<SiteBuilderFileSearchItem>();
        foreach (var file in files)
        {
            // a file without a name has no key to be listed by
            var key = GetSearchKey(contentType, file);
            if (key != null && !keys.Contains(key) && ReadSearchItem(file, storageProvider) is { } item)
            {
                item.Key = key;
                keys.Add(key);
                items.Add(item);
            }
        }

        return items;
    }

    public virtual Task<SiteBuilderOperationResult<PageHistory>> GetFilePageHistoryAsync(SiteBuilderFilePageHistoryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetFilePageHistoryInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<PageHistory>> GetFilePageHistoryInternalAsync(SiteBuilderFilePageHistoryRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var contentType = string.IsNullOrEmpty(request.ContentType) ? PagesContentType : request.ContentType;
        var contentRequest = new SiteBuilderContentRequest
        {
            StoreId = request.StoreId,
            ContentType = contentType,
            Pages = string.IsNullOrEmpty(request.Path) ? [] : [request.Path],
            HistoryTake = request.Take,
            HistoryAfter = request.After,
        };
        if (await CheckRequestAsync(contentRequest, request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<PageHistory>();
        }

        var repository = await gitContentPolicy.GetRepositoryAsync(request.StoreId, cancellationToken);
        if (repository == null)
        {
            // there is no history to show: the blob flow overwrites in place and keeps no versions
            return SiteBuilderOperationResult.NotFound().As<PageHistory>();
        }

        if (string.IsNullOrEmpty(request.Path))
        {
            // a page that does not exist yet — the blade asking about one it is about to create
            return SiteBuilderOperationResult.Success(PageHistory.Empty);
        }

        var location = gitContentOptions.Value.GetPageLocation(repository, contentType, request.Path, user?.Identity?.Name);
        var history = await gitContentHistory.GetHistoryAsync(location.Repository, location.RepoPath, new GitHistoryQuery
        {
            BaseBranch = (await gitHubConnectionService.GetConnectionAsync(cancellationToken)).BaseBranch,
            PublishedDepth = request.Take,
            After = request.After,
        }, cancellationToken);

        // "mine" is the server's contribution: the repository knows which branches hold a commit, only
        // this end knows which of those branches is the caller's.
        return SiteBuilderOperationResult.Success(PageHistory.From(history, location.Branch));
    }

    /// <summary>Store access first, so a user bound to another store cannot tell from a refused request which stores exist.</summary>
    protected virtual async Task<SiteBuilderOperationResult> CheckRequestAsync(SiteBuilderContentRequest request, IList<string> bindingErrors,
        ClaimsPrincipal user, CancellationToken cancellationToken) =>
        await authorizationService.CheckStoreOperationAsync(user, request)
        ?? await requestValidator.CheckOperationRequestAsync(request, bindingErrors, cancellationToken);

    protected virtual ThemeTemplate CreateTemplate(string key)
    {
        var template = AbstractTypeFactory<ThemeTemplate>.TryCreateInstance();
        template.Key = key;
        template.Type = key.Split('.')[0];
        template.IsDefault = !key.Contains('.');

        return template;
    }

    /// <summary>The schema files of a kind as one JSON object, each by its key, as stored.</summary>
    protected virtual async Task<string> GetSchemaFilesAsync(string storeId, string theme, string kind)
    {
        var themeName = await GetCurrentThemeName(storeId, theme);
        var schemasFolder = $"{themeName}/config/schemas/{kind}";
        var basePath = GetContentBasePath(storeId, Themes, themeName);
        var storageProvider = blobContentStorageProviderFactory.CreateProvider(basePath);
        var allFiles = await storageProvider.SearchAsync(schemasFolder, null);
        var files = allFiles.Results.Where(x => x.Name.EndsWith(JsonExtension, StringComparison.OrdinalIgnoreCase));
        var response = string.Join(", ", files.Select(file => $"\"{Path.GetFileNameWithoutExtension(file.Name)}\": {GetContent(file, storageProvider)}"));

        return $"{{{response}}}";
    }

    protected virtual async Task<JArray> GetSchemaCatalogAsync(string storeId, string theme, string kind, bool filterInternal)
    {
        var themeName = await GetCurrentThemeName(storeId, theme);
        var schemasFolder = $"{themeName}/config/schemas/{kind}";
        var basePath = GetContentBasePath(storeId, Themes, themeName);
        var storageProvider = blobContentStorageProviderFactory.CreateProvider(basePath);
        var allFiles = await storageProvider.SearchAsync(schemasFolder, null);
        var files = allFiles.Results.Where(x =>
            x.Type != FolderEntryType &&
            x.Name.EndsWith(JsonExtension, StringComparison.OrdinalIgnoreCase));

        var result = new JArray();
        foreach (var file in files)
        {
            var key = Path.GetFileNameWithoutExtension(file.Name);
            if (filterInternal && key.StartsWith('_'))
            {
                continue;
            }

            try
            {
                var raw = GetContent(file, storageProvider);
                var json = JObject.Parse(raw);
                // Static section schemas describe page-level settings panels — they are not
                // selectable as content sections. Hide them from the catalog; their `settings[]`
                // is merged into the template response by `MergeStaticSectionsIntoTemplate`.
                if (kind == Sections && SiteBuilderSchemas.IsStaticEntry(json))
                {
                    continue;
                }

                var entry = new JObject { ["key"] = key };
                SiteBuilderSchemas.CopySchemaMetadata(json, entry, kind);
                result.Add(entry);
            }
            catch
            {
                // Skip files that cannot be parsed.
            }
        }

        return result;
    }

    protected virtual async Task<string> MergeStaticSectionsIntoTemplateAsync(string templateJson, string storeId, string themeName)
    {
        var sectionsFolder = $"{themeName}/config/schemas/{Sections}";
        var basePath = GetContentBasePath(storeId, Themes, themeName);
        var storageProvider = blobContentStorageProviderFactory.CreateProvider(basePath);
        var sectionFiles = (await storageProvider.SearchAsync(sectionsFolder, null)).Results
            .Where(x => x.Type != FolderEntryType && x.Name.EndsWith(JsonExtension, StringComparison.OrdinalIgnoreCase));

        var schemas = new Dictionary<string, string>();
        foreach (var file in sectionFiles)
        {
            try
            {
                schemas[Path.GetFileNameWithoutExtension(file.Name)] = GetContent(file, storageProvider);
            }
            catch
            {
                // Skip unreadable section files.
            }
        }

        return SiteBuilderSchemas.MergeStaticSectionsIntoTemplate(templateJson, schemas);
    }

    protected virtual async Task<SiteBuilderOperationResult<SiteBuilderFileContent>> ReadFileFromBlobAsync(SiteBuilderFileReadRequest request)
    {
        var basePath = GetContentBasePath(request.StoreId, request.ContentType, request.Theme);
        if (!string.IsNullOrEmpty(request.Path))
        {
            var storageProvider = blobContentStorageProviderFactory.CreateProvider(basePath);

            var blobInfo = await storageProvider.GetBlobInfoAsync(publishingService.GetRelativeDraftUrl(request.Path, request.Draft));
            if (blobInfo == null && request.Draft)
            {
                blobInfo = await storageProvider.GetBlobInfoAsync(publishingService.GetRelativeDraftUrl(request.Path, false));
            }

            if (blobInfo != null)
            {
                var result = AbstractTypeFactory<SiteBuilderFileContent>.TryCreateInstance();
                result.Stream = await storageProvider.OpenReadAsync(blobInfo.RelativeUrl);
                result.Name = blobInfo.Name;
                return SiteBuilderOperationResult.Success(result);
            }
        }

        var notFound = AbstractTypeFactory<SiteBuilderFileContent>.TryCreateInstance();
        notFound.BasePath = basePath;

        return SiteBuilderOperationResult.NotFound(notFound);
    }

    /// <summary>
    /// Reads a page out of the content repository, or <c>null</c> when the repository does not have
    /// it. Without an explicit ref: the editor's draft of this page when they have one, otherwise the
    /// published version. That fallback is what keeps the contract of the blob-backed endpoint — "the
    /// draft, else what is live" — intact, so the callers of this URL never learn that pages moved to
    /// git.
    /// </summary>
    protected virtual async Task<string> ReadPageFromGitAsync(string repository, SiteBuilderFileReadRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var location = gitContentOptions.Value.GetPageLocation(repository, request.ContentType, request.Path, user?.Identity?.Name);

        if (!string.IsNullOrEmpty(request.Ref))
        {
            // an exact commit — what a preview link points at
            return await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, request.Ref, cancellationToken);
        }

        if (request.Draft)
        {
            var onBranch = await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, location.Branch, cancellationToken);
            if (onBranch != null)
            {
                return onBranch;
            }
        }

        var baseBranch = (await gitHubConnectionService.GetConnectionAsync(cancellationToken)).BaseBranch;

        return await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, baseBranch, cancellationToken);
    }

    /// <summary>The page's name and permalink from its settings; <c>null</c> for a file that cannot be read as a page.</summary>
    protected virtual SiteBuilderFileSearchItem ReadSearchItem(BlobEntry entry, IBlobContentStorageProvider provider)
    {
        try
        {
            using var reader = new StreamReader(provider.OpenRead(entry.RelativeUrl));
            var content = reader.ReadToEnd();
            dynamic json = JsonConvert.DeserializeObject(content);
            var item = AbstractTypeFactory<SiteBuilderFileSearchItem>.TryCreateInstance();
            item.Name = json.settings.name.ToString();
            item.PreviewUrl = json.settings.permalink.ToString();
            item.Path = entry.RelativeUrl;
            return item;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The files, not the folders, of the search whose name contains the pattern taken literally.</summary>
    protected virtual async Task<IList<BlobEntry>> FindFilesAsync(IBlobContentStorageProvider storageProvider, string folder, string keyword, string pattern)
    {
        var regexp = pattern == null ? null : new Regex(Regex.Escape(pattern), RegexOptions.None, TimeSpan.FromSeconds(1));
        var found = await storageProvider.SearchAsync(folder, keyword);

        return found.Results.Where(x => x.Type != FolderEntryType && (regexp?.IsMatch(x.Name) ?? true)).ToList();
    }

    private static string GetSearchKey(string contentType, BlobEntry entry) =>
        contentType == null ? Path.GetFileNameWithoutExtension(entry.Name) : $"{contentType}::{entry.RelativeUrl}";

    protected virtual string GetContentBasePath(string storeId, string contentType, string theme) =>
        pathResolver.GetContentBasePath(contentType, storeId, theme);

    protected virtual async Task<string> GetCurrentThemeName(string storeId, string themeName) =>
        string.IsNullOrEmpty(themeName) ? await themeStorageFactory.GetThemeNameAsync(storeId) : themeName;

    private static SiteBuilderContentRequest ThemeRequest(SiteBuilderThemeQueryRequest request) =>
        new() { StoreId = request.StoreId, ContentType = Themes, Theme = request.Theme, Paths = [] };

    private static string GetContent(BlobEntry entry, IBlobContentStorageProvider provider)
    {
        using var reader = new StreamReader(provider.OpenRead(entry.RelativeUrl));
        return reader.ReadToEnd();
    }

    private static async Task<JObject> ReadJsonBlobAsync(IBlobContentStorageProvider storageProvider, string relativeUrl)
    {
        await using var stream = await storageProvider.OpenReadAsync(relativeUrl);
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync();

        return string.IsNullOrWhiteSpace(json) ? [] : JObject.Parse(json);
    }
}
