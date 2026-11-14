using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Data.Services;

[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The services come from dependency injection; a parameter object would only hide them and change the constructor that subclasses call.")]
public class DesignerOperationService(
    IAuthorizationService authorizationService,
    IContentPermissionService contentPermissionService,
    IValidator<SiteBuilderContentRequest> requestValidator,
    IValidator<DesignerSaveRequest> designerSaveValidator,
    IValidator<DesignerDiscardRequest> designerDiscardValidator,
    IGroupedPageService groupedPageService,
    ISiteBuilderSharedComponentPlacementValidator sharedComponentPlacementValidator,
    IDesignerContentService designerContentService,
    IThemeDraftService themeDraftService) : IDesignerOperationService
{
    private const string Themes = "themes";
    private const string CatalogRequiredError = "Theme files are checked against the storefront's schema catalog: name it with catalogHash (preview the storefront first).";
    private const string PageVersionRequiredError = "Read the page content and send its ETag before saving.";
    private const string ThemeFileVersionRequiredError = "Read each theme file with designer/theme-file and send its ETag before saving.";

    public virtual Task<SiteBuilderOperationResult<ThemeFileContent>> ReadThemeFileAsync(DesignerThemeFileRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ReadThemeFileInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<ThemeFileContent>> ReadThemeFileInternalAsync(DesignerThemeFileRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var contentRequest = new SiteBuilderContentRequest
        {
            StoreId = request.StoreId,
            Files = [new SiteBuilderContentFile { ContentType = Themes, Path = request.Path }],
        };
        if (await CheckRequestAsync(contentRequest, request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<ThemeFileContent>();
        }

        return SiteBuilderOperationResult.Success(await themeDraftService.ReadAsync(request.StoreId, request.Path, cancellationToken));
    }

    public virtual Task<SiteBuilderOperationResult<DesignerSaveResult>> SaveAsync(DesignerSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SaveInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<DesignerSaveResult>> SaveInternalAsync(DesignerSaveOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var page = request.Changes?.Page;
        var themeFiles = request.Changes?.ThemeFiles ?? [];
        var contentRequest = new SiteBuilderContentRequest { StoreId = request.StoreId, CatalogHash = request.CatalogHash };
        var (group, refused) = await CheckDesignerRequestAsync(contentRequest, themeFiles.Select(x => x?.Path), page?.GroupId, designerSaveValidator,
            request.Changes, user, cancellationToken);
        if (refused != null)
        {
            return refused.As<DesignerSaveResult>();
        }

        if (!TryParseDesignerFiles(page, themeFiles, out var files, out var parseError))
        {
            return SiteBuilderOperationResult.Invalid(parseError).As<DesignerSaveResult>();
        }

        contentRequest.Files = files;
        if (await CheckDesignerChangesAsync(contentRequest, request, group, page, themeFiles, user, cancellationToken) is { } refusedChanges)
        {
            return refusedChanges.As<DesignerSaveResult>();
        }

        var command = CreateDesignerSaveCommand(request, group, files, themeFiles);

        return await SaveDesignerChangesAsync(command, request.DryRun, user, cancellationToken);
    }

    /// <summary>The request with the files it saves, then the page, then the theme files.</summary>
    private async Task<SiteBuilderOperationResult> CheckDesignerChangesAsync(SiteBuilderContentRequest contentRequest, DesignerSaveOperationRequest request,
        GroupedSiteBuilderPage group, DesignerSavePage page, IList<DesignerSaveFile> themeFiles, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        return await requestValidator.CheckOperationRequestAsync(contentRequest, request.BindingErrors, cancellationToken)
               ?? await CheckDesignerPageAsync(request.StoreId, group, page, user, cancellationToken)
               ?? await CheckDesignerThemeFilesAsync(request.StoreId, themeFiles, contentRequest.Files, request.CatalogHash, cancellationToken);
    }

    public virtual Task<SiteBuilderOperationResult<ContentSaveResult>> DiscardAsync(DesignerDiscardOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return DiscardInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<ContentSaveResult>> DiscardInternalAsync(DesignerDiscardOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var page = request.Changes?.Page;
        var themePaths = request.Changes?.ThemeFiles ?? [];
        var contentRequest = new SiteBuilderContentRequest { StoreId = request.StoreId };
        var (group, refused) = await CheckDesignerRequestAsync(contentRequest, themePaths, page?.GroupId, designerDiscardValidator, request.Changes, user,
            cancellationToken);
        if (refused != null)
        {
            return refused.As<ContentSaveResult>();
        }

        contentRequest.Files = themePaths.Select(x => new SiteBuilderContentFile { ContentType = Themes, Path = x }).ToList();
        var filePage = GetDesignerFilePage(page);
        if (filePage != null)
        {
            contentRequest.ContentType = filePage.ContentType;
            contentRequest.Pages = [filePage.Path];
        }

        if (await requestValidator.CheckOperationRequestAsync(contentRequest, request.BindingErrors, cancellationToken) is { } invalid)
        {
            return invalid.As<ContentSaveResult>();
        }

        var command = AbstractTypeFactory<DesignerDiscardCommand>.TryCreateInstance();
        command.StoreId = request.StoreId;
        command.SaveId = request.SaveId;
        command.Group = group;
        command.FilePage = filePage;
        command.ThemeFiles = themePaths;
        command.PageETag = request.ExpectedPageVersion;
        command.ThemeFileETags = request.ExpectedThemeFileVersions;
        try
        {
            return request.DryRun
                ? SiteBuilderOperationResult.DryRun<ContentSaveResult>(await designerContentService.PreviewDiscardAsync(command, user, cancellationToken))
                : SiteBuilderOperationResult.Success(await designerContentService.DiscardAsync(command, user, cancellationToken));
        }
        catch (InvalidDataException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message).As<ContentSaveResult>();
        }
        catch (SiteBuilderContentConflictException ex)
        {
            return SiteBuilderOperationResult.Conflict(ex.Message).As<ContentSaveResult>();
        }
    }

    public virtual Task<SiteBuilderOperationResult> ValidateThemeFilesAsync(string storeId, IList<SiteBuilderContentFile> files, bool draft,
        string catalogHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        return ValidateThemeFilesInternalAsync(storeId, files, draft, catalogHash, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult> ValidateThemeFilesInternalAsync(string storeId, IList<SiteBuilderContentFile> files, bool draft,
        string catalogHash, CancellationToken cancellationToken)
    {
        if (!DesignerThemeFiles.AreNamedOnce(files.Select(x => x.Path).ToList()))
        {
            return SiteBuilderOperationResult.Invalid(DesignerThemeFiles.NamingError);
        }

        var contents = new Dictionary<string, JToken>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            contents[file.Path] = file.Content;
        }

        try
        {
            await designerContentService.ValidateThemeFilesAsync(storeId, contents, draft, catalogHash, cancellationToken);
            return SiteBuilderOperationResult.Success();
        }
        catch (InvalidDataException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message);
        }
        catch (ThemeValidationException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message, ex.Errors);
        }
    }

    /// <summary>Store access first, so a user bound to another store cannot tell from a refused request which stores exist.</summary>
    protected virtual async Task<SiteBuilderOperationResult> CheckRequestAsync(SiteBuilderContentRequest request, IList<string> bindingErrors,
        ClaimsPrincipal user, CancellationToken cancellationToken) =>
        await authorizationService.CheckStoreOperationAsync(user, request)
        ?? await requestValidator.CheckOperationRequestAsync(request, bindingErrors, cancellationToken);

    /// <returns>The group of the page the request names, and the refusal of the request, if any.</returns>
    protected virtual async Task<(GroupedSiteBuilderPage Group, SiteBuilderOperationResult Refused)> CheckDesignerRequestAsync<T>(
        SiteBuilderContentRequest request, IEnumerable<string> themePaths, string groupId, IValidator<T> validator, T value, ClaimsPrincipal user,
        CancellationToken cancellationToken)
        where T : class, new()
    {
        if ((await authorizationService.CheckStoreOperationAsync(user, request) ?? await CheckThemeFilesAccessAsync(themePaths, user)) is { } forbidden)
        {
            return (null, forbidden);
        }

        if (await validator.CheckOperationRequestAsync(value ?? new T(), cancellationToken) is { } invalidRequest)
        {
            return (null, invalidRequest);
        }

        if (string.IsNullOrEmpty(groupId))
        {
            return (null, null);
        }

        var group = await groupedPageService.GetByIdAsync(groupId);
        return (group, await CheckDesignerGroupAccessAsync(request.StoreId, group, user));
    }

    protected virtual async Task<SiteBuilderOperationResult> CheckThemeFilesAccessAsync(IEnumerable<string> themePaths, ClaimsPrincipal user)
    {
        var permissions = themePaths.Select(contentPermissionService.GetThemeFileEditPermission).ToList();

        return await contentPermissionService.HasPermissionsAsync(user, permissions) ? null : SiteBuilderOperationResult.Forbidden();
    }

    protected virtual async Task<SiteBuilderOperationResult> CheckDesignerGroupAccessAsync(string storeId, GroupedSiteBuilderPage group, ClaimsPrincipal user)
    {
        if (group == null)
        {
            return SiteBuilderOperationResult.NotFound();
        }

        return await contentPermissionService.CanAccessPageAsync(user, group, storeId) ? null : SiteBuilderOperationResult.Forbidden();
    }

    protected virtual async Task<SiteBuilderOperationResult> CheckDesignerPageAsync(string storeId, GroupedSiteBuilderPage group, DesignerSavePage page,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (group == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(page.ETag))
        {
            return SiteBuilderOperationResult.VersionRequired(PageVersionRequiredError);
        }

        var placement = await sharedComponentPlacementValidator.ValidateAsync(user, storeId, page.Content, cancellationToken);
        if (placement.IsForbidden)
        {
            return SiteBuilderOperationResult.Forbidden();
        }

        return placement.Error != null ? SiteBuilderOperationResult.Invalid(placement.Error) : null;
    }

    protected virtual async Task<SiteBuilderOperationResult> CheckDesignerThemeFilesAsync(string storeId, IList<DesignerSaveFile> themeFiles,
        IList<SiteBuilderContentFile> files, string catalogHash, CancellationToken cancellationToken)
    {
        if (themeFiles.Any(x => string.IsNullOrWhiteSpace(x.ETag)))
        {
            return SiteBuilderOperationResult.VersionRequired(ThemeFileVersionRequiredError);
        }

        var themeSaveFiles = files.Where(IsThemeFile).ToList();
        if (themeSaveFiles.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrEmpty(catalogHash))
        {
            return SiteBuilderOperationResult.Invalid(CatalogRequiredError);
        }

        var validation = await ValidateThemeFilesAsync(storeId, themeSaveFiles, draft: true, catalogHash, cancellationToken);
        return validation.Succeeded ? null : validation;
    }

    protected virtual DesignerSaveCommand CreateDesignerSaveCommand(DesignerSaveOperationRequest request, GroupedSiteBuilderPage group,
        IList<SiteBuilderContentFile> files, IList<DesignerSaveFile> themeFiles)
    {
        var page = request.Changes?.Page;
        var command = AbstractTypeFactory<DesignerSaveCommand>.TryCreateInstance();
        command.StoreId = request.StoreId;
        command.SaveId = request.SaveId;
        command.Group = group;
        command.PageContent = page?.Content;
        command.PageETag = page?.ETag?.Trim();
        command.FilePage = files.FirstOrDefault(x => !IsThemeFile(x));
        command.ThemeFiles = themeFiles;
        command.CatalogHash = request.CatalogHash;

        return command;
    }

    protected virtual async Task<SiteBuilderOperationResult<DesignerSaveResult>> SaveDesignerChangesAsync(DesignerSaveCommand command, bool dryRun,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        try
        {
            return dryRun
                ? SiteBuilderOperationResult.DryRun<DesignerSaveResult>(await designerContentService.PreviewSaveAsync(command, user, cancellationToken))
                : SiteBuilderOperationResult.Success(await designerContentService.SaveAsync(command, user, cancellationToken));
        }
        catch (SiteBuilderContentConflictException ex)
        {
            return SiteBuilderOperationResult.Conflict(ex.Message).As<DesignerSaveResult>();
        }
        catch (KeyNotFoundException)
        {
            return SiteBuilderOperationResult.NotFound().As<DesignerSaveResult>();
        }
        catch (InvalidDataException ex)
        {
            return SiteBuilderOperationResult.Invalid(ex.Message).As<DesignerSaveResult>();
        }
    }

    private static SiteBuilderContentFile GetDesignerFilePage(DesignerSavePage page) =>
        string.IsNullOrEmpty(page?.Path) ? null : new SiteBuilderContentFile { ContentType = page.Type.ToLowerInvariant(), Path = page.Path };

    private static bool IsThemeFile(SiteBuilderContentFile file) => string.Equals(file?.ContentType, Themes, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the JSON of the file page and the theme files as they are, dates included.</summary>
    private static bool TryParseDesignerFiles(DesignerSavePage page, IList<DesignerSaveFile> themeFiles, out List<SiteBuilderContentFile> files,
        out string error)
    {
        files = [];
        error = null;
        var named = themeFiles.Select(x => (x.Path, Type: Themes, x.Content)).ToList();
        if (!string.IsNullOrEmpty(page?.Path))
        {
            named.Insert(0, (page.Path, page.Type.ToLowerInvariant(), page.Content));
        }

        foreach (var (path, type, content) in named)
        {
            if (!TryParseJson(content, out var token))
            {
                error = $"\"{path}\" is not a JSON document.";
                return false;
            }

            files.Add(new SiteBuilderContentFile { Path = path, ContentType = type, Content = token });
        }

        return true;
    }

    private static bool TryParseJson(string content, out JToken token)
    {
        token = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            using var reader = new JsonTextReader(new StringReader(content)) { DateParseHandling = DateParseHandling.None };
            var parsed = JToken.ReadFrom(reader);
            // a theme file is saved as sent, so nothing may follow the document that was checked
            if (reader.Read())
            {
                return false;
            }

            token = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
