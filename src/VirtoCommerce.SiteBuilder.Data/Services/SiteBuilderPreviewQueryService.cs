using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Authorization;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.StoreModule.Core.Services;

namespace VirtoCommerce.SiteBuilder.Data.Services;

[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The services come from dependency injection; a parameter object would only hide them and change the constructor that subclasses call.")]
public class SiteBuilderPreviewQueryService(
    IAuthorizationService authorizationService,
    IValidator<SiteBuilderContentRequest> requestValidator,
    IStoreService storeService,
    ISettingsManager settingsManager,
    IGroupedPageService groupedPageService,
    ISiteBuilderContentQueryService contentQueryService,
    IGitContentPolicy gitContentPolicy,
    IGitContentRepository gitContentRepository,
    IGitHubConnectionService gitHubConnectionService,
    IOptions<GitContentOptions> gitContentOptions) : ISiteBuilderPreviewQueryService
{
    private const string Themes = "themes";
    private const string PagesContentType = "pages";
    private const string DefaultPreviewPath = "/designer-preview";
    private const string PagesTemplateType = "pages";
    private const string ContentPageType = "page";

    public virtual Task<SiteBuilderOperationResult<DesignerStorefront>> GetStorefrontAsync(SiteBuilderOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetStorefrontInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<DesignerStorefront>> GetStorefrontInternalAsync(SiteBuilderOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var contentRequest = new SiteBuilderContentRequest { StoreId = request.StoreId, ContentType = Themes, Paths = [] };
        if (await CheckRequestAsync(contentRequest, request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<DesignerStorefront>();
        }

        var storefront = AbstractTypeFactory<DesignerStorefront>.TryCreateInstance();
        storefront.Url = await GetStorefrontUrlAsync(request.StoreId);
        storefront.PreviewPath = await GetPreviewPathAsync();

        return SiteBuilderOperationResult.Success(storefront);
    }

    public virtual Task<SiteBuilderOperationResult<SiteBuilderPreviewLink>> GetFilePagePreviewAsync(SiteBuilderFilePagePreviewRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetFilePagePreviewInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<SiteBuilderPreviewLink>> GetFilePagePreviewInternalAsync(SiteBuilderFilePagePreviewRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        // The link goes straight to the storefront: a branch name in it would stop meaning what the link said.
        var contentRequest = new SiteBuilderContentRequest
        {
            StoreId = request.StoreId,
            ContentType = request.ContentType ?? PagesContentType,
            Pages = [request.Path],
            Ref = request.Ref,
        };
        if (await CheckRequestAsync(contentRequest, request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<SiteBuilderPreviewLink>();
        }

        var repository = await gitContentPolicy.GetRepositoryAsync(request.StoreId, cancellationToken);
        if (repository == null)
        {
            return SiteBuilderOperationResult.NotFound().As<SiteBuilderPreviewLink>();
        }

        var location = gitContentOptions.Value.GetPageLocation(repository, request.ContentType, request.Path, user?.Identity?.Name);

        var gitRef = request.Ref ?? await DraftOrPublishedHeadAsync(location.Repository, location.Branch, cancellationToken);
        if (gitRef == null)
        {
            return SiteBuilderOperationResult.NotFound(AbstractTypeFactory<SiteBuilderPreviewLink>.TryCreateInstance());
        }

        var storeUrl = await GetStorefrontUrlAsync(request.StoreId);
        if (string.IsNullOrEmpty(storeUrl))
        {
            return SiteBuilderOperationResult.Invalid(
                $"Nowhere to preview \"{request.Path}\": store \"{request.StoreId}\" has no storefront url. " +
                $"Fill in the store's Url (or Secure URL), or set {ModuleConstants.Settings.General.StoreUrl.Name} to override it.").As<SiteBuilderPreviewLink>();
        }

        var previewPath = await GetPreviewPathAsync();
        var pageId = EncodePageId(request.StoreId, request.ContentType, request.Path);

        var link = AbstractTypeFactory<SiteBuilderPreviewLink>.TryCreateInstance();
        link.Url = $"{storeUrl.TrimEnd('/')}{previewPath}?pageId={Uri.EscapeDataString(pageId)}&ref={Uri.EscapeDataString(gitRef)}";
        link.Ref = gitRef;

        return SiteBuilderOperationResult.Success(link);
    }

    public virtual Task<SiteBuilderOperationResult<SiteBuilderPreviewLink>> GetGroupedPagePreviewAsync(GroupedPageQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetGroupedPagePreviewInternalAsync(request, user);
    }

    private async Task<SiteBuilderOperationResult<SiteBuilderPreviewLink>> GetGroupedPagePreviewInternalAsync(GroupedPageQueryRequest request,
        ClaimsPrincipal user)
    {
        var group = string.IsNullOrEmpty(request.GroupId) ? null : await groupedPageService.GetNoCloneAsync(request.GroupId);
        if (group == null)
        {
            return SiteBuilderOperationResult.NotFound().As<SiteBuilderPreviewLink>();
        }

        if (await authorizationService.CheckStoreOperationAsync(user, group) is { } forbidden)
        {
            return forbidden.As<SiteBuilderPreviewLink>();
        }

        var storeUrl = await GetStorefrontUrlAsync(group.StoreId);
        if (string.IsNullOrEmpty(storeUrl))
        {
            return NoStorefrontUrl(group.StoreId).As<SiteBuilderPreviewLink>();
        }

        // the storefront's preview page reads a page of the database by its group id, as the designer opens it
        var link = AbstractTypeFactory<SiteBuilderPreviewLink>.TryCreateInstance();
        link.Url = $"{storeUrl.TrimEnd('/')}{await GetPreviewPathAsync()}?pageId={Uri.EscapeDataString(group.Id)}";

        return SiteBuilderOperationResult.Success(link);
    }

    public virtual Task<SiteBuilderOperationResult<SiteBuilderPreviewLink>> GetTemplatePreviewAsync(SiteBuilderTemplatePreviewRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetTemplatePreviewInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<SiteBuilderPreviewLink>> GetTemplatePreviewInternalAsync(SiteBuilderTemplatePreviewRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var schemasRequest = AbstractTypeFactory<SiteBuilderSchemaQueryRequest>.TryCreateInstance();
        schemasRequest.StoreId = request.StoreId;
        schemasRequest.Theme = request.Theme;
        schemasRequest.Kind = ModuleConstants.SchemaKinds.Templates;
        schemasRequest.BindingErrors = request.BindingErrors;
        var schemas = await contentQueryService.GetSchemasAsync(schemasRequest, user, cancellationToken);
        if (!schemas.Succeeded)
        {
            return schemas.As<SiteBuilderPreviewLink>();
        }

        if (request.TemplateKey == null || !ThemeStorage.IsTemplateKey(request.TemplateKey))
        {
            return SiteBuilderOperationResult.Invalid($"\"{request.TemplateKey}\" is not a template key.").As<SiteBuilderPreviewLink>();
        }

        var pagePath = GetTemplatePreviewPath(ParseSchemas(schemas.Value), request.TemplateKey.Split('.')[0]);
        if (string.IsNullOrWhiteSpace(pagePath))
        {
            return SiteBuilderOperationResult.NotFound().As<SiteBuilderPreviewLink>();
        }

        var storeUrl = await GetStorefrontUrlAsync(request.StoreId);
        if (string.IsNullOrEmpty(storeUrl))
        {
            return NoStorefrontUrl(request.StoreId).As<SiteBuilderPreviewLink>();
        }

        var link = AbstractTypeFactory<SiteBuilderPreviewLink>.TryCreateInstance();
        link.Url = $"{storeUrl.TrimEnd('/')}/{pagePath.Trim().TrimStart('/')}";

        return SiteBuilderOperationResult.Success(link);
    }

    /// <summary>Store access first, so a user bound to another store cannot tell from a refused request which stores exist.</summary>
    protected virtual async Task<SiteBuilderOperationResult> CheckRequestAsync(SiteBuilderContentRequest request, IList<string> bindingErrors,
        ClaimsPrincipal user, CancellationToken cancellationToken) =>
        await authorizationService.CheckStoreOperationAsync(user, request)
        ?? await requestValidator.CheckOperationRequestAsync(request, bindingErrors, cancellationToken);

    /// <summary>
    /// The sample page of the template schema of the type, found as the designer finds it: the schema the storefront previews the type
    /// with, else the one named by the type; for pages, the schema of all pages.
    /// </summary>
    protected virtual string GetTemplatePreviewPath(JObject schemas, string type)
    {
        var entries = schemas.Properties().Where(x => x.Value is JObject).Select(x => (Key: x.Name, Schema: (JObject)x.Value)).ToList();
        var found = entries.Find(x => GetString(x.Schema.SelectToken("previewMessage.templateKey")) == type ||
                                      (x.Key == type && GetString(x.Schema["type"]) != PagesTemplateType));
        if (found.Schema == null && type == ContentPageType)
        {
            found = entries.Find(x => GetString(x.Schema["type"]) == PagesTemplateType && x.Schema.SelectToken("previewMessage.templateKey") == null);
        }

        return GetString(found.Schema?["previewUrl"]);
    }

    /// <summary>
    /// Where this store's storefront lives, for a preview link.
    /// <para>
    /// The store itself is the source: it already records its storefront url, and it is the only answer
    /// that can differ per store. <see cref="ModuleConstants.Settings.General.StoreUrl"/> is a single
    /// value for the whole installation, so on a platform with more than one store it can only be right
    /// about one of them — it stays supported as a deliberate override (a staging host, a tunnel) and is
    /// consulted first for installations that already rely on it.
    /// </para>
    /// </summary>
    protected virtual async Task<string> GetStorefrontUrlAsync(string storeId)
    {
        var configured = await GetSettingAsync(ModuleConstants.Settings.General.StoreUrl);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }

        if (string.IsNullOrEmpty(storeId))
        {
            return null;
        }

        var store = await storeService.GetNoCloneAsync(storeId);
        // https first: a preview link is opened in a browser next to the platform, which is served over
        // https itself, and a mixed-content redirect is a dead end
        var fromStore = FirstFilled(store?.SecureUrl, store?.Url);

        return fromStore?.Trim();
    }

    protected virtual async Task<string> GetPreviewPathAsync() =>
        FirstFilled(await GetSettingAsync(ModuleConstants.Settings.General.StorePreviewPath), DefaultPreviewPath);

    protected virtual async Task<string> GetSettingAsync(SettingDescriptor descriptor)
    {
        var setting = await settingsManager.GetObjectSettingAsync(descriptor.Name);
        return setting?.Value?.ToString();
    }

    private async Task<string> DraftOrPublishedHeadAsync(string repository, string workBranch, CancellationToken cancellationToken) =>
        await gitContentRepository.GetBranchHeadShaAsync(repository, workBranch, cancellationToken)
        ?? await gitContentRepository.GetBranchHeadShaAsync(repository, (await gitHubConnectionService.GetConnectionAsync(cancellationToken)).BaseBranch,
            cancellationToken);

    private static SiteBuilderOperationResult NoStorefrontUrl(string storeId) =>
        SiteBuilderOperationResult.Invalid($"Store \"{storeId}\" has no storefront url to preview on. " +
                                           $"Fill in the store's Url (or Secure URL), or set {ModuleConstants.Settings.General.StoreUrl.Name} to override it.");

    // base64("<storeId>::<contentType>::<relativeUrl>") with '=' replaced by '-', matching what the
    // builder puts in a preview link and what the storefront decodes (DesignerPreviewController).
    // The url stays relative to its content root — for a blog article that is the blogs folder, which
    // the storefront re-adds when it looks the file up.
    private static string EncodePageId(string storeId, string contentType, string path)
    {
        var raw = $"{storeId}::{(string.IsNullOrEmpty(contentType) ? PagesContentType : contentType)}::{path.Replace('\\', '/').TrimStart('/')}";
        return Convert.ToBase64String(PageJson.Encoding.GetBytes(raw)).Replace('=', '-');
    }

    private static string FirstFilled(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string GetString(JToken token) => token is JValue { Type: JTokenType.String } value ? (string)value : null;

    private static JObject ParseSchemas(string json)
    {
        try
        {
            return JObject.Parse(json);
        }
        catch (JsonReaderException)
        {
            // one schema that is not JSON spoils the whole listing: no sample page is known
            return [];
        }
    }
}
