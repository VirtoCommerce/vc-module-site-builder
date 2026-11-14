using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using VirtoCommerce.AssetsModule.Core.Assets;
using VirtoCommerce.ContentModule.Core.Model;
using VirtoCommerce.ContentModule.Core.Services;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.SiteBuilder.Web.Extensions;
using VirtoCommerce.SiteBuilder.Web.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.DistributedLock;
using VirtoCommerce.Platform.Core.Events;

namespace VirtoCommerce.SiteBuilder.Web.Controllers.Api
{
    [Route("api/site-builder")]
    [Authorize]
    [RepositoryOwnershipConflict]
    [GitContentFailure]
    public class SiteBuilderController(
            IContentPathResolver pathResolver,
            IBlobContentStorageProviderFactory blobContentStorageProviderFactory,
            IPublishingService publishingService,
            IEventPublisher eventPublisher,
            IOptions<GitContentOptions> gitContentOptions,
            IGitHubConnectionService gitHubConnectionService,
            IGitContentPolicy gitContentPolicy,
            IGitContentRepository gitContentRepository,
            IAuthorizationService authorizationService,
            IDistributedLock distributedLock,
            IValidator<SiteBuilderContentRequest> requestValidator,
            IContentHistory contentHistory,
            IDesignerOperationService designerOperationService,
            IContentPermissionService contentPermissionService,
            IFilePageDraftService filePageDraftService,
            IThemeStorageFactory themeStorageFactory,
            ISiteBuilderContentGuard contentGuard,
            ISiteBuilderContentQueryService contentQueryService,
            ISiteBuilderPreviewQueryService previewQueryService,
            ISiteBuilderPublishStatusQueryService publishStatusQueryService,
            IFilePageOperationService filePageOperationService
            )
        : Controller
    {
        private const string Themes = "themes";
        // The content types the git flow covers are pages and blogs — the same .page files, blogs living
        // in a subfolder (see GitPageLocation.ContentPath). Themes, schemas and settings stay in blob
        // storage. PagesContentType is the default assumed when a caller sends no type at all.
        private const string PagesContentType = "pages";
        private const string JsonContentType = "application/json";
        private const string SchemaKindTemplates = ModuleConstants.SchemaKinds.Templates;
        private const string SchemaKindObjects = ModuleConstants.SchemaKinds.Objects;
        // The blob name of a draft in the flow that came before git: the page's name with this appended.
        // Only the legacy-draft cleanup deals in these; everywhere else GitPageLocation strips the suffix.
        private const string DraftSuffix = ModuleConstants.DraftSuffix;
        private const string FolderEntryType = "folder";
        private const int MaxLegacyDraftPageSize = 200;
        private const string LegacyDraftOnlyCopy = "only-copy";
        private const string LegacyDraftHasChanges = "has-changes";
        private const string LegacyDraftsOfPagesError = "Leftover drafts are listed for pages and blog articles: the content type must be pages or blogs.";
        private const string SaveAction = "save";


        [HttpGet]
        [Route("template")]
        public async Task<ActionResult> GetTemplate(string storeId, string theme, string path, string type, bool draft = false, [FromQuery(Name = "ref")] string gitRef = null)
        {
            var request = new SiteBuilderFileReadRequest
            {
                StoreId = storeId,
                Theme = theme,
                ContentType = type,
                Path = path,
                Draft = draft,
                Ref = gitRef,
                BindingErrors = ModelState.GetBindingErrors(),
            };
            var result = await contentQueryService.ReadFileAsync(request, User, HttpContext.RequestAborted);
            if (result.Status == SiteBuilderOperationStatus.NotFound && result.Value is { } notFound)
            {
                return notFound.Ref != null
                    ? NotFound(new { templatePath = path, gitRef = notFound.Ref })
                    : NotFound(new { basePath = notFound.BasePath, templatePath = path });
            }

            return result.Succeeded ? FileContent(result.Value) : result.ToRefusal();
        }

        private async Task<string> GetBaseBranchAsync() => (await gitHubConnectionService.GetConnectionAsync(HttpContext.RequestAborted)).BaseBranch;

        private GitPageFileLocation GitLocation(string repository, string type, string path) =>
            gitContentOptions.Value.GetPageLocation(repository, type, path, User?.Identity?.Name);

        private ActionResult FileContent(SiteBuilderFileContent file) =>
            file.Stream != null ? File(file.Stream, MimeTypeResolver.ResolveContentType(file.Name)) : Content(file.Text, JsonContentType);

        [HttpGet]
        [Route("settings")]
        public async Task<ActionResult> GetSettings(string storeId, string theme)
        {
            var result = await contentQueryService.GetBuilderSettingsAsync(ThemeQuery(storeId, theme), User, HttpContext.RequestAborted);

            return result.Succeeded ? FileContent(result.Value) : result.ToRefusal();
        }

        [HttpGet]
        [Route("templates")]
        public async Task<ActionResult> GetTemplates(string storeId, string theme)
        {
            var result = await contentQueryService.GetSchemasAsync(SchemaQuery(storeId, theme, SchemaKindTemplates), User, HttpContext.RequestAborted);

            return result.Succeeded ? Content(result.Value, JsonContentType) : result.ToRefusal();
        }

        [HttpGet]
        [Route("objects")]
        public async Task<ActionResult> GetObjects(string storeId, string theme)
        {
            var result = await contentQueryService.GetSchemasAsync(SchemaQuery(storeId, theme, SchemaKindObjects), User, HttpContext.RequestAborted);

            return result.Succeeded ? Ok(result.Value) : result.ToRefusal();
        }

        [HttpGet]
        [Route("sections")]
        public async Task<ActionResult> GetSectionsSettings(string storeId, string theme)
        {
            var result = await contentQueryService.GetSectionSchemasAsync(ThemeQuery(storeId, theme), User, HttpContext.RequestAborted);

            return result.Succeeded ? Content(result.Value, JsonContentType) : result.ToRefusal();
        }

        [HttpGet]
        [Route("schemas")]
        public async Task<ActionResult> GetSchemasCatalog(string storeId, string theme)
        {
            var result = await contentQueryService.GetSchemaCatalogAsync(ThemeQuery(storeId, theme), User, HttpContext.RequestAborted);

            return result.Succeeded ? Content(result.Value, JsonContentType) : result.ToRefusal();
        }

        [HttpGet]
        [Route("schemas/{kind}/{key}")]
        public async Task<ActionResult> GetSchemaByKey(string storeId, string theme, string kind, string key)
        {
            var request = SchemaQuery(storeId, theme, kind);
            request.Key = key;
            var result = await contentQueryService.GetSchemaAsync(request, User, HttpContext.RequestAborted);
            if (result.Status == SiteBuilderOperationStatus.NotFound)
            {
                return NotFound(new { kind, key });
            }

            return result.Succeeded ? Content(result.Value, JsonContentType) : result.ToRefusal();
        }

        private SiteBuilderThemeQueryRequest ThemeQuery(string storeId, string theme) =>
            new() { StoreId = storeId, Theme = theme, BindingErrors = ModelState.GetBindingErrors() };

        private SiteBuilderSchemaQueryRequest SchemaQuery(string storeId, string theme, string kind) =>
            new() { StoreId = storeId, Theme = theme, Kind = kind, BindingErrors = ModelState.GetBindingErrors() };

        [HttpGet]
        [Route("~/api/pagebuilder/search")]
        [Obsolete("Themes made for Page Builder name this address in their section schemas. Use api/site-builder/search.")]
        public Task<ActionResult<string>> SearchByPageBuilderAddress(string storeId, string theme, string type, string folder, string pattern = null, string keyword = null)
        {
            return Search(storeId, theme, type, folder, pattern, keyword);
        }

        [HttpGet]
        [Route("search")]
        public async Task<ActionResult<string>> Search(string storeId, string theme, string type, string folder, string pattern = null, string keyword = null)
        {
            var request = new SiteBuilderFileSearchRequest
            {
                StoreId = storeId,
                Theme = theme,
                ContentType = type,
                Folder = folder,
                Pattern = pattern,
                Keyword = keyword,
                BindingErrors = ModelState.GetBindingErrors(),
            };
            var result = await contentQueryService.SearchFilesAsync(request, User, HttpContext.RequestAborted);

            return result.Succeeded ? Content(FileSearchContent(result.Value)) : result.ToRefusal();
        }

        private static string FileSearchContent(IList<SiteBuilderFileSearchItem> items)
        {
            var jsonSettings = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() };
            var files = items.Select(x =>
                $"\"{x.Key}\": {JsonConvert.SerializeObject(new ContentModel { Name = x.Name, PreviewUrl = x.PreviewUrl, Path = x.Path }, jsonSettings)}");

            return $"{{{string.Join(", ", files)}}}";
        }

        [HttpPost]
        [Authorize(ModuleConstants.Security.Permissions.Update)]
        [Route("save")]
        public async Task<ActionResult> SaveTemplates(string storeId, string theme, [FromBody] SaveFilesModel value, [FromQuery] bool draft = false,
            [FromQuery] string catalogHash = null)
        {
            var request = new SiteBuilderContentRequest { StoreId = storeId, Theme = theme, CatalogHash = catalogHash };
            if (await CheckStoreAccessAsync(request) is { } forbidden)
            {
                return forbidden;
            }

            if (!TryParseFiles(value, out var files))
            {
                return BadRequest(new { error = "files must be a JSON list of files." });
            }

            var gitFlow = await gitContentPolicy.IsEnabledForStoreAsync(storeId, HttpContext.RequestAborted);
            var themeFiles = files.Where(IsThemeFile).ToList();
            if (!await CanSaveFilesAsync(files, themeFiles, draft, gitFlow))
            {
                return Forbid();
            }

            request.Files = files.ConvertAll(ToContentFile);
            if (await ValidateAsync(request) is { } invalid)
            {
                return invalid;
            }

            if (GuardPages(files) is { } unsafePages)
            {
                return unsafePages;
            }

            if (themeFiles.Count > 0 && (await designerOperationService.ValidateThemeFilesAsync(storeId, themeFiles.ConvertAll(ToContentFile), draft, catalogHash,
                    HttpContext.RequestAborted)).ToRefusal() is { } refusedThemeFiles)
            {
                return refusedThemeFiles;
            }

            if (!gitFlow || !draft)
            {
                await SaveFilesTo(storeId, theme, files, draft);
                return Ok();
            }

            return await SaveDraftToGitAsync(storeId, theme, files);
        }

        private async Task<bool> CanSaveFilesAsync(List<SaveFileModel> files, List<SaveFileModel> themeFiles, bool draft, bool gitFlow)
        {
            if (IsPublication(files, draft, gitFlow) && !await HasPermissionsAsync([ModuleConstants.Security.Permissions.Publish]))
            {
                return false;
            }

            return await HasPermissionsAsync(themeFiles.Select(x => contentPermissionService.GetThemeFileEditPermission(x.Path)));
        }

        // With the git flow on, live belongs to the CI deploy and editors publish through a pull request.
        // Without git the designer saves theme settings straight to live, which needs no publish permission.
        private static bool IsPublication(List<SaveFileModel> files, bool draft, bool gitFlow) =>
            !draft && (gitFlow || files.Exists(file => file != null && GitPageLocation.IsPageContent(file.Type)));

        private async Task<bool> HasPermissionsAsync(IEnumerable<string> permissions) =>
            await contentPermissionService.HasPermissionsAsync(User, permissions);

        /// <summary>Sanitizes the HTML of the pages in place, so that what is written is sanitized.</summary>
        private BadRequestObjectResult GuardPages(List<SaveFileModel> files)
        {
            var errors = files
                .Where(x => GitPageLocation.IsPageContent(x.Type) && x.Content is JToken)
                .SelectMany(x => contentGuard.GuardPage((JToken)x.Content, x.Path, catalog: null).Errors)
                .ToList();

            return errors.Count > 0 ? BadRequest(new { error = SiteBuilderContentGuardExtensions.GetMessage(errors), errors }) : null;
        }

        private static bool IsThemeFile(SaveFileModel file) => string.Equals(file?.Type, Themes, StringComparison.OrdinalIgnoreCase);

        private static SiteBuilderContentFile ToContentFile(SaveFileModel file) =>
            file == null ? null : new SiteBuilderContentFile { ContentType = file.Type, Path = file.Path, Content = AsToken(file.Content) };

        private static bool TryParseFiles(SaveFilesModel value, out List<SaveFileModel> files)
        {
            files = [];
            if (value?.Files == null)
            {
                return false;
            }

            try
            {
                var parsed = JsonConvert.DeserializeObject<List<SaveFileModel>>(value.Files);
                files = parsed ?? [];
                return parsed != null;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private async Task<ActionResult> SaveDraftToGitAsync(string storeId, string theme, List<SaveFileModel> files)
        {
            // A draft is a commit on the editor's own work branch, so two editors of one page keep apart
            // their drafts and previews.
            var pages = files.Where(x => GitPageLocation.IsPageContent(x.Type)).ToList();

            var saved = new List<GitPageCommit>();
            if (pages.Count > 0)
            {
                var repository = await gitContentPolicy.GetRepositoryAsync(storeId, HttpContext.RequestAborted);
                foreach (var page in pages)
                {
                    saved.Add(await filePageDraftService.CommitDraftAsync(storeId, repository, ToContentFile(page), SaveAction, User, HttpContext.RequestAborted));
                }
            }

            // Themes, schemas and everything else still live in blob storage — only pages and blogs moved
            // to git.
            var others = files.Except(pages).ToList();
            if (others.Count > 0)
            {
                await SaveFilesTo(storeId, theme, others, draft: true);
            }

            return Ok(new { pages = saved });
        }

        [HttpGet]
        [Route("designer/theme-file")]
        [ProducesResponseType(typeof(ThemeFileContent), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<ThemeFileContent>> GetDesignerThemeFile([FromQuery] string storeId, [FromQuery] string path)
        {
            var request = new DesignerThemeFileRequest { StoreId = storeId, Path = path, BindingErrors = ModelState.GetBindingErrors() };

            return (await designerOperationService.ReadThemeFileAsync(request, User, HttpContext.RequestAborted)).ToActionResult();
        }

        [HttpGet]
        [Route("designer/storefront")]
        [ProducesResponseType(typeof(DesignerStorefront), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<DesignerStorefront>> GetDesignerStorefront([FromQuery] string storeId)
        {
            var request = new SiteBuilderOperationRequest { StoreId = storeId, BindingErrors = ModelState.GetBindingErrors() };

            return (await previewQueryService.GetStorefrontAsync(request, User, HttpContext.RequestAborted)).ToActionResult();
        }

        [HttpPost]
        [Authorize(ModuleConstants.Security.Permissions.Update)]
        [Route("designer/save")]
        [ProducesResponseType(typeof(DesignerSaveResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
        [ProducesResponseType(StatusCodes.Status428PreconditionRequired)]
        public async Task<ActionResult<DesignerSaveResult>> SaveDesignerChanges([FromQuery] string storeId, [FromBody] DesignerSaveRequest value,
            [FromQuery] string catalogHash = null)
        {
            var request = new DesignerSaveOperationRequest
            {
                StoreId = storeId,
                Changes = value,
                CatalogHash = catalogHash,
                SaveId = Request.GetOrCreateSaveId(),
                BindingErrors = ModelState.GetBindingErrors(),
            };

            return (await designerOperationService.SaveAsync(request, User, HttpContext.RequestAborted)).ToActionResult();
        }

        [HttpPost]
        [Authorize(ModuleConstants.Security.Permissions.Update)]
        [Route("designer/discard")]
        [ProducesResponseType(typeof(ContentSaveResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<ContentSaveResult>> DiscardDesignerChanges([FromQuery] string storeId, [FromBody] DesignerDiscardRequest value)
        {
            var request = new DesignerDiscardOperationRequest
            {
                StoreId = storeId,
                Changes = value,
                SaveId = Request.GetOrCreateSaveId(),
                BindingErrors = ModelState.GetBindingErrors(),
            };

            return (await designerOperationService.DiscardAsync(request, User, HttpContext.RequestAborted)).ToActionResult();
        }

        /// <summary>
        /// Ships the editor's own draft, never the bytes the caller sends, and writes it to live storage of a repository the platform manages.
        /// </summary>
        [HttpPost]
        [Authorize(ModuleConstants.Security.Permissions.Publish)]
        [Route("git/publish")]
        public async Task<ActionResult> GitPublish(string storeId, string path, string type, bool rebase = false)
        {
            var request = new FilePagePublishOperationRequest
            {
                StoreId = storeId,
                ContentType = type,
                Path = path,
                Rebase = rebase,
                BindingErrors = ModelState.GetBindingErrors(),
            };
            var result = await filePageOperationService.PublishAsync(request, User, HttpContext.RequestAborted);

            return result.Succeeded ? ShipmentResponse(result.Value, emptyWithoutGit: false) : PageDocumentRefusal(result);
        }

        /// <summary>
        /// Promotes a page to production: the state of its file on <c>BaseBranch</c>, placed onto
        /// <c>ReleaseBranch</c> as its own commit, never a merge of one shared branch into the other.
        /// </summary>
        [HttpPost]
        [Authorize(ModuleConstants.Security.Permissions.Promote)]
        [Route("git/promote")]
        public async Task<ActionResult> GitPromote(string storeId, string path, string type)
        {
            var request = new FilePageOperationRequest { StoreId = storeId, ContentType = type, Path = path, BindingErrors = ModelState.GetBindingErrors() };
            var result = await filePageOperationService.PromoteAsync(request, User, HttpContext.RequestAborted);

            return result.Succeeded ? ShipmentResponse(result.Value, emptyWithoutGit: false) : result.ToRefusal();
        }

        /// <summary>
        /// Removes the page from live storage of a repository the platform manages only once the removal is merged, so a revert stays a rollback.
        /// </summary>
        [HttpPost]
        [Authorize(ModuleConstants.Security.Permissions.Publish)]
        [Route("git/unpublish")]
        public async Task<ActionResult> GitUnpublish(string storeId, string path, string type)
        {
            var request = new FilePageShipOperationRequest { StoreId = storeId, ContentType = type, Path = path, BindingErrors = ModelState.GetBindingErrors() };
            var result = await filePageOperationService.UnpublishAsync(request, User, HttpContext.RequestAborted);
            if (result.Status == SiteBuilderOperationStatus.NotFound && result.Value != null)
            {
                return NotFound(new { templatePath = path });
            }

            return result.Succeeded ? ShipmentResponse(result.Value, emptyWithoutGit: true) : result.ToRefusal();
        }

        /// <summary>
        /// The answer to a publish, an unpublish or a promotion. A merge that conflicts is a 409 telling whether publishing the draft
        /// again with <c>rebase=true</c> is a way out.
        /// </summary>
        private ActionResult ShipmentResponse(FilePageShipment shipment, bool emptyWithoutGit)
        {
            if (!shipment.GitFlow && emptyWithoutGit)
            {
                return Ok();
            }

            if (shipment.Merge == null)
            {
                return Ok(new { state = shipment.State.ToString() });
            }

            if (shipment.Merge.State == GitPublishState.Conflict)
            {
                return Conflict(new
                {
                    error = shipment.ConflictError,
                    pullRequest = shipment.Merge.PullRequestNumber,
                    url = shipment.Merge.Url,
                    canRebase = shipment.CanRebase,
                });
            }

            return Ok(new { state = shipment.Merge.State.ToString(), pullRequest = shipment.Merge.PullRequestNumber, url = shipment.Merge.Url });
        }

        /// <summary>A page that is not a page document, or can run script, is refused with the problems alone.</summary>
        private static ActionResult PageDocumentRefusal(SiteBuilderOperationResult result) =>
            result.Status == SiteBuilderOperationStatus.Invalid && result.Errors != null
                ? new BadRequestObjectResult(new { errors = result.Errors })
                : result.ToRefusal();

        /// <summary>
        /// The shape the builder's toolbar expects, answered from git: <c>published</c> is "the page
        /// exists in the production branch", <c>hasChanges</c> is "this editor's branch says something
        /// different", <c>pending</c> is "a pull request for it is open", and <c>awaitingMerge</c> is
        /// "that pull request is not going to merge itself".
        /// <para>
        /// The answer also names the flow in effect, because a client has to behave differently under
        /// each — the admin blade saves to git and unpublishes through git on the git flow — and asking
        /// here costs it nothing. Both flows answer in the same shape, so a caller reads one contract.
        /// </para>
        /// </summary>
        [HttpGet]
        [Route("git/publish-status")]
        public async Task<ActionResult<PagePublishStatus>> GitPublishStatus(string storeId, string path, string type)
        {
            var request = new SiteBuilderFilePageQueryRequest { StoreId = storeId, ContentType = type, Path = path, BindingErrors = ModelState.GetBindingErrors() };

            return (await publishStatusQueryService.GetFilePageStatusAsync(request, User, HttpContext.RequestAborted)).ToActionResult();
        }

        /// <summary>
        /// The versions of a page: commits on the production branch, and commits on any other branch that
        /// the production branch does not have yet.
        /// <para>
        /// Versions are found by the page's file history rather than by branch name, which is what makes an
        /// edit done outside the builder visible here at all: the module names its own work branches
        /// <c>designer/{user}/{slug}</c>, an edit made through Claude lives on a <c>content/*</c> branch, and
        /// neither has to know about the other's namespace for the commit to show up in this list.
        /// </para>
        /// <para>
        /// Reading history needs no permission beyond being signed in, as with every other read here — but
        /// note it does surface other editors' unpublished drafts, which blob storage never had.
        /// </para>
        /// </summary>
        [HttpGet]
        [Route("git/history")]
        public async Task<ActionResult> GitHistory(string storeId, string path, string type, int? take = null, string after = null)
        {
            var request = new SiteBuilderFilePageHistoryRequest
            {
                StoreId = storeId,
                ContentType = type,
                Path = path,
                Take = take,
                After = after,
                BindingErrors = ModelState.GetBindingErrors(),
            };

            return (await contentQueryService.GetFilePageHistoryAsync(request, User, HttpContext.RequestAborted)).ToActionResult();
        }

        /// <summary>
        /// Continues editing from an earlier version: the content of <paramref name="sha"/> becomes a new
        /// commit on this editor's own work branch. Forward only: history stays append-only, and the branch
        /// stays one editor's.
        /// </summary>
        [HttpPost]
        [Authorize(ModuleConstants.Security.Permissions.Update)]
        [Route("git/restore-version")]
        public async Task<ActionResult> GitRestoreVersion(string storeId, string path, string type, [BindRequired] string sha)
        {
            var request = new FilePageRestoreVersionOperationRequest
            {
                StoreId = storeId,
                ContentType = type,
                Path = path,
                Sha = sha,
                BindingErrors = ModelState.GetBindingErrors(),
            };
            var result = await filePageOperationService.RestoreVersionAsync(request, User, HttpContext.RequestAborted);
            if (result.Status == SiteBuilderOperationStatus.NotFound && result.Value != null)
            {
                return NotFound(new { templatePath = path, gitRef = sha });
            }

            return result.Succeeded
                ? Ok(new { branch = result.Value.Branch, commitSha = result.Value.CommitSha, restoredFrom = result.Value.RestoredFrom })
                : PageDocumentRefusal(result);
        }

        /// <summary>
        /// Redirects to the storefront's preview of this page at an exact commit: the editor's draft if
        /// they have one, otherwise what is published. A commit sha is immutable, so the link keeps
        /// showing what it showed when it was made.
        /// </summary>
        [HttpGet]
        [Route("git/preview")]
        public async Task<ActionResult> GitPreview(string storeId, string path, string type, [FromQuery(Name = "ref")] string requestedRef = null)
        {
            var request = new SiteBuilderFilePagePreviewRequest
            {
                StoreId = storeId,
                ContentType = type,
                Path = path,
                Ref = requestedRef,
                BindingErrors = ModelState.GetBindingErrors(),
            };
            var result = await previewQueryService.GetFilePagePreviewAsync(request, User, HttpContext.RequestAborted);
            if (result.Status == SiteBuilderOperationStatus.NotFound && result.Value != null)
            {
                return NotFound(new { templatePath = path });
            }

            return result.Succeeded ? Redirect(result.Value.Url) : result.ToRefusal();
        }

        /// <summary>Refused when nothing is published, as the draft is then the only copy</summary>
        [HttpPost]
        [Route("discard-draft")]
        [Authorize(ModuleConstants.Security.Permissions.Update)]
        public async Task<ActionResult> DiscardDraft(string storeId, string path, string type, string theme = null)
        {
            var request = new FileDraftDiscardOperationRequest
            {
                StoreId = storeId,
                ContentType = type,
                Path = path,
                Theme = theme,
                SaveId = Request.GetOrCreateSaveId(),
                BindingErrors = ModelState.GetBindingErrors(),
            };
            var result = await filePageOperationService.DiscardDraftAsync(request, User, HttpContext.RequestAborted);
            if (result.Status == SiteBuilderOperationStatus.Invalid && result.Value is { } refused)
            {
                return refused.KeptInGit ? BadRequest(result.Error) : Conflict(result.Error);
            }

            return result.ToRefusal() ?? NoContent();
        }

        /// <summary>
        /// Throws this editor's draft of the page away, so the next read starts from what is published.
        /// The way out of a conflict, and the only one that does not quietly pick a winner.
        /// </summary>
        [HttpPost]
        [Authorize(ModuleConstants.Security.Permissions.Update)]
        [Route("git/discard-draft")]
        public async Task<ActionResult> GitDiscardDraft(string storeId, string path, string type)
        {
            var request = new FilePageDraftOperationRequest { StoreId = storeId, ContentType = type, Path = path, BindingErrors = ModelState.GetBindingErrors() };

            return (await filePageOperationService.DiscardGitDraftAsync(request, User, HttpContext.RequestAborted)).ToRefusal() ?? Ok();
        }

        /// <summary>
        /// The blob draft files this store carried over from the flow that came before git, and whether
        /// each one still holds anything the repository does not.
        /// <para>
        /// Opting a store into the git flow leaves its "<c>foo.page-draft</c>" blobs exactly where they
        /// were, and two very different things end up wearing that one name: dead weight, for a page that
        /// has since been saved from the designer and now lives in git — and the only copy of somebody's
        /// unpublished work, for a page that has not, because <see cref="GetTemplate"/> still serves those
        /// from blob. Telling the two apart is all this endpoint does; deleting is a separate call, over
        /// the paths the caller picked out of this answer.
        /// </para>
        /// <para>
        /// Scanning <c>pages</c> covers blogs too: blob storage keeps them in a subfolder of the pages
        /// root and the repository mirrors that layout, so a draft found under <c>blogs/</c> maps to the
        /// repository path it would have been committed to anyway.
        /// </para>
        /// </summary>
        [HttpGet]
        [Route("git/legacy-drafts")]
        public async Task<ActionResult> GetLegacyDrafts(string storeId, string type, string theme, int skip = 0, int take = 50)
        {
            var contentType = type ?? PagesContentType;
            if (await CheckRequestAsync(storeId, contentType, theme, []) is { } refused)
            {
                return refused;
            }

            // The request check holds the type to pages only for a request that names some, and this one names none.
            if (!GitPageLocation.IsPageContent(contentType))
            {
                return BadRequest(new { error = LegacyDraftsOfPagesError });
            }

            var repository = await gitContentPolicy.GetRepositoryAsync(storeId, HttpContext.RequestAborted);
            if (repository == null)
            {
                // On the blob flow a "-draft" file IS the draft, not a leftover. There is nothing to clean
                // up here, and handing back a list would invite a caller to delete live content.
                return NotFound();
            }

            var storageProvider = blobContentStorageProviderFactory.CreateProvider(
                GetContentBasePath(storeId, contentType, await GetCurrentThemeName(storeId, theme)));

            var drafts = await FindDraftBlobsAsync(storageProvider);

            // Walking folders is cheap; comparing each file against the repository is not. So page first
            // and only look up the window that was asked for.
            var window = drafts
                .OrderBy(x => x.RelativeUrl, StringComparer.OrdinalIgnoreCase)
                .Skip(Math.Max(skip, 0))
                .Take(Math.Clamp(take, 1, MaxLegacyDraftPageSize))
                .ToList();

            var items = new List<LegacyDraftInfo>();
            foreach (var draft in window)
            {
                items.Add(await DescribeLegacyDraftAsync(repository, storageProvider, contentType, draft));
            }

            return Ok(new { totalCount = drafts.Count, skip, take, items });
        }

        /// <summary>
        /// Deletes blob draft files left over from the pre-git flow — exactly the paths it is given, and
        /// nothing else.
        /// <para>
        /// Deliberately not <c>DELETE api/content/{type}/{store}</c>, which is the endpoint a caller
        /// reaches for first. That one resolves every url it is handed into both the draft and the
        /// published name and removes whichever exist, so asking it to delete "foo.page-draft" takes the
        /// live "foo.page" down with it.
        /// </para>
        /// </summary>
        [HttpPost]
        [Authorize(ModuleConstants.Security.Permissions.Delete)]
        [Route("git/legacy-drafts/delete")]
        public async Task<ActionResult> DeleteLegacyDrafts(string storeId, string type, string theme,
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] LegacyDraftsDeleteRequest request)
        {
            // An empty body is a dry run over nothing; a body that does not bind is refused by the request check.
            var contentType = type ?? PagesContentType;
            request ??= new LegacyDraftsDeleteRequest();
            var contentRequest = new SiteBuilderContentRequest
            {
                StoreId = storeId,
                ContentType = contentType,
                Theme = theme,
                LegacyDrafts = request.Paths ?? [],
            };
            if (await CheckRequestAsync(contentRequest) is { } refused)
            {
                return refused;
            }

            var repository = await gitContentPolicy.GetRepositoryAsync(storeId, HttpContext.RequestAborted);
            if (repository == null)
            {
                return NotFound();
            }

            var paths = (request.Paths ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var storageProvider = blobContentStorageProviderFactory.CreateProvider(
                GetContentBasePath(storeId, contentType, await GetCurrentThemeName(storeId, theme)));

            var (deletable, skipped) = await SortLegacyDraftsAsync(repository, paths, storageProvider, contentType);

            if (!request.DryRun && deletable.Count > 0)
            {
                await storageProvider.RemoveAsync([.. deletable]);
            }

            return Ok(new { dryRun = request.DryRun, deleted = deletable, skipped });
        }

        /// <summary>
        /// Splits the given draft paths into the ones whose page has another copy — those are a cleanup —
        /// and the ones that are the page's whole copy, which are reported back untouched.
        /// </summary>
        private async Task<(List<string> Deletable, List<object> Skipped)> SortLegacyDraftsAsync(
            string repository, IEnumerable<string> paths, IBlobContentStorageProvider storageProvider, string contentType)
        {
            var deletable = new List<string>();
            var skipped = new List<object>();

            foreach (var path in paths)
            {
                var repoPath = GitLocation(repository, contentType, path).RepoPath;
                var inGit = await gitContentRepository.ReadFileAsync(repository, repoPath, await GetBaseBranchAsync(), HttpContext.RequestAborted);
                var published = inGit ?? await storageProvider.ReadTextAsync(WithoutDraftSuffix(path));

                if (published != null && await storageProvider.ReadTextAsync(path) is { } inBlob && !IsSamePageDocument(inBlob, published))
                {
                    skipped.Add(new { path, reason = LegacyDraftHasChanges });
                }
                else if (published != null)
                {
                    deletable.Add(path);
                }
                else
                {
                    // Neither the repository nor a published blob holds this page, so this file is the
                    // whole of it: deleting it would not be a cleanup, it would be the loss of the page.
                    // Saving it once from the designer puts it in git and makes it deletable.
                    //
                    // A page that is merely absent from the repository is NOT this case — a store that
                    // opted in keeps pages it never re-saved, and there the published blob still serves
                    // the page after the draft goes.
                    skipped.Add(new { path, reason = LegacyDraftOnlyCopy });
                }
            }

            return (deletable, skipped);
        }

        /// <summary>
        /// Duplicates a page inside the content repository: a commit of the same document under a new
        /// name, on the copying editor's work branch.
        /// <para>
        /// It exists because the blob copy cannot be right here.
        /// <c>POST api/content/{type}/{store}/copy-file</c> always writes the duplicate as a
        /// "<c>-draft</c>" blob — the blob flow's way of saying "not published yet" — and on the git flow
        /// that produces a file no storefront serves, which no publish will ever pick up, and which the
        /// next deploy leaves behind. Unpublished here means "on a branch, not on the base branch", so
        /// that is what a copy has to be.
        /// </para>
        /// <para>
        /// A store not on the git flow gets <c>404</c>: the blob copy is correct for it, and the caller
        /// falls back to it rather than this endpoint reimplementing it.
        /// </para>
        /// </summary>
        [HttpPost]
        [Authorize(ModuleConstants.Security.Permissions.Create)]
        [Route("git/copy")]
        public async Task<ActionResult> GitCopyPage(string storeId, string type, string srcPath, string destPath)
        {
            var request = new FilePageCopyOperationRequest
            {
                StoreId = storeId,
                ContentType = type,
                Path = srcPath,
                TargetPath = destPath,
                BindingErrors = ModelState.GetBindingErrors(),
            };
            var result = await filePageOperationService.CopyAsync(request, User, HttpContext.RequestAborted);
            if (result.Status == SiteBuilderOperationStatus.Invalid && result.Value != null)
            {
                return BadRequest(new { error = result.Error, srcPath });
            }

            return result.ToActionResult();
        }

        /// <summary>The page's own name, whichever of the two names the caller happens to hold.</summary>
        private static string WithoutDraftSuffix(string path) =>
            path.EndsWith(DraftSuffix, StringComparison.OrdinalIgnoreCase) ? path[..^DraftSuffix.Length] : path;

        /// <summary>
        /// Whether one page still has a leftover blob draft from the flow that came before git, and
        /// whether that file is safe to throw away.
        /// <para>
        /// The per-page question, asked by the blade for the page it has open. The store-wide inventory
        /// answers the same thing for everything at once, which is a folder walk plus a repository read
        /// per file — far too much for opening one page.
        /// </para>
        /// </summary>
        [HttpGet]
        [Route("git/legacy-draft")]
        public async Task<ActionResult> GetLegacyDraft(string storeId, string type, string path, string theme)
        {
            var request = PageRequest(storeId, type, PathsOf(path));
            request.Theme = theme;
            if (await CheckRequestAsync(request) is { } refused)
            {
                return refused;
            }

            var repository = await gitContentPolicy.GetRepositoryAsync(storeId, HttpContext.RequestAborted);
            if (repository == null)
            {
                return NotFound();
            }

            if (path.IsNullOrEmpty())
            {
                // a page being created has no path, and therefore nothing left over
                return Ok(new LegacyDraftInfo());
            }

            var contentType = type ?? PagesContentType;
            var storageProvider = blobContentStorageProviderFactory.CreateProvider(
                GetContentBasePath(storeId, contentType, await GetCurrentThemeName(storeId, theme)));

            // The blade may hold either name for the page — the list strips the suffix for display but
            // keeps it on the url of a page that only ever had a draft.
            var blobInfo = await storageProvider.GetBlobInfoAsync(LegacyDraftPathOf(path));

            return Ok(blobInfo == null
                ? new LegacyDraftInfo()
                : await DescribeLegacyDraftAsync(repository, storageProvider, contentType, blobInfo));
        }

        /// <summary>The draft blob name of a page, whichever of the two names the caller already holds.</summary>
        private static string LegacyDraftPathOf(string path) =>
            path.EndsWith(DraftSuffix, StringComparison.OrdinalIgnoreCase) ? path : path + DraftSuffix;

        private async Task<LegacyDraftInfo> DescribeLegacyDraftAsync(string repository, IBlobContentStorageProvider storageProvider,
            string contentType, BlobEntry draft)
        {
            var repoPath = GitLocation(repository, contentType, draft.RelativeUrl).RepoPath;
            var inGit = await gitContentRepository.ReadFileAsync(repository, repoPath, await GetBaseBranchAsync(), HttpContext.RequestAborted);
            var inBlob = await storageProvider.ReadTextAsync(draft.RelativeUrl);
            // The published blob matters as much as the repository here. A store that opted in keeps
            // every page it had, and one never saved from the designer since is in blob storage only:
            // for it, "foo.page" is the live page and this draft is an edit on top of it.
            var published = await storageProvider.ReadTextAsync(WithoutDraftSuffix(draft.RelativeUrl));

            return new LegacyDraftInfo
            {
                Exists = true,
                BlobPath = draft.RelativeUrl,
                RepoPath = repoPath,
                ModifiedDate = draft.ModifiedDate,
                ExistsInGit = inGit != null,
                // Nothing else holds this page: deleting the draft would not tidy up after the page,
                // it would be the page going away.
                IsOnlyCopy = inGit == null && published == null,
                // Compared against whatever currently serves the page — the repository when it has it,
                // the published blob otherwise. Comparing against git alone called every blob-only
                // page's draft "changed", which is no answer at all for the stores being cleaned up.
                DiffersFromCurrent = !IsSamePageDocument(inBlob, inGit ?? published),
            };
        }

        /// <summary>
        /// Whether two page documents say the same thing, whatever their formatting.
        /// <para>
        /// Compared as documents rather than as bytes, unlike publish status. Legacy drafts were written
        /// with a four-space indent and CRLF line endings, so a byte comparison would report every single
        /// one of them as changed and the flag would carry no information. The question being asked here
        /// is only whether the draft still holds content the repository has not got.
        /// </para>
        /// <para>
        /// A draft that will not parse counts as different: it cannot be shown to be safe to delete, and
        /// "unknown" belongs on the cautious side of a decision that destroys the only copy.
        /// </para>
        /// </summary>
        private static bool IsSamePageDocument(string left, string right)
        {
            if (left == null || right == null)
            {
                return left == null && right == null;
            }

            try
            {
                return JToken.DeepEquals(JToken.Parse(left), JToken.Parse(right));
            }
            catch (JsonReaderException)
            {
                return false;
            }
        }

        /// <summary>
        /// Every "-draft" blob under the provider's root. The provider lists one folder at a time, so the
        /// recursion is ours; folders already seen are not re-entered, because a provider that reports a
        /// folder as its own child would otherwise spin here forever.
        /// </summary>
        private static async Task<IList<BlobEntry>> FindDraftBlobsAsync(IBlobContentStorageProvider storageProvider)
        {
            var found = new List<BlobEntry>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folders = new Queue<string>();
            folders.Enqueue(null); // the provider's root

            while (folders.Count > 0)
            {
                var entries = (await storageProvider.SearchAsync(folders.Dequeue(), null)).Results;

                foreach (var entry in entries)
                {
                    var isFolder = entry.Type.EqualsIgnoreCase(FolderEntryType);

                    if (isFolder && visited.Add(entry.RelativeUrl))
                    {
                        folders.Enqueue(entry.RelativeUrl);
                    }
                    else if (!isFolder && entry.Name.EndsWith(DraftSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(entry);
                    }
                }
            }

            return found;
        }

        private static string[] PathsOf(params string[] paths) => paths.Where(path => !path.IsNullOrEmpty()).ToArray();

        private static SiteBuilderContentRequest PageRequest(string storeId, string type, params string[] pages) =>
            new() { StoreId = storeId, ContentType = type ?? PagesContentType, Pages = pages };

        private Task<ActionResult> CheckRequestAsync(string storeId, string type, string theme, IList<string> paths = null) =>
            CheckRequestAsync(new SiteBuilderContentRequest
            {
                StoreId = storeId,
                ContentType = type,
                Theme = theme,
                Paths = paths ?? [],
            });

        /// <summary>
        /// Store access first, so a user bound to another store cannot tell from a 400 which stores exist.
        /// The permission itself is on the action's [Authorize] attributes.
        /// </summary>
        private Task<ActionResult> CheckRequestAsync(SiteBuilderContentRequest request) =>
            authorizationService.CheckStoreRequestAsync(User, requestValidator, request, ModelState, HttpContext.RequestAborted);

        private Task<ActionResult> CheckStoreAccessAsync(SiteBuilderContentRequest request) => authorizationService.CheckStoreAccessAsync(User, request);

        private async Task<ActionResult> ValidateAsync(SiteBuilderContentRequest request)
        {
            return await requestValidator.ValidateRequestAsync(request, ModelState, HttpContext.RequestAborted);
        }

        private static JToken AsToken(object content)
        {
            return content as JToken ?? (content == null ? JValue.CreateNull() : JToken.FromObject(content));
        }

        private async Task SaveFilesTo(string storeId, string theme, IReadOnlyCollection<SaveFileModel> files, bool draft, CancellationToken cancellationToken = default)
        {
            var providers = new Dictionary<string, IBlobContentStorageProvider>();

            var settings = new JsonSerializerSettings { Formatting = Formatting.Indented };
            var themeName = await GetCurrentThemeName(storeId, theme);

            var changedFiles = new Dictionary<string, List<GenericChangedEntry<FileEntity>>>();
            var themeFiles = new List<(string Path, string Content)>();

            // a theme publication deletes the drafts it moved live
            if (files.Any(x => string.Equals(x.Type, Themes, StringComparison.OrdinalIgnoreCase)))
            {
                await distributedLock.ExecuteAsync(ThemeStorage.GetLockResource(storeId), _ => WriteFilesAsync(), cancellationToken: cancellationToken);
            }
            else
            {
                await WriteFilesAsync();
            }

            foreach (var (type, entries) in changedFiles)
            {
                await eventPublisher.Publish(new SiteBuilderContentChangedEvent(type, entries), cancellationToken);
            }

            if (themeFiles.Count > 0)
            {
                await RecordThemeFilesAsync(storeId, themeFiles, draft, cancellationToken);
            }

            async Task WriteFilesAsync()
            {
                foreach (var file in files)
                {
                    var type = file.Type.ToLowerInvariant();
                    if (!providers.TryGetValue(type, out var storageProvider))
                    {
                        storageProvider = blobContentStorageProviderFactory.CreateProvider(GetContentBasePath(storeId, type, themeName));
                        providers[type] = storageProvider;
                    }
                    var content = file.Content;
                    var targetPath = publishingService.GetRelativeDraftUrl(file.Path, draft);
                    await using var targetStream = await storageProvider.OpenWriteAsync(targetPath);
                    await using var writer = new StreamWriter(targetStream);
                    var stringContent = JsonConvert.SerializeObject(content, settings);
                    await writer.WriteAsync(stringContent);
                    if (type == Themes)
                    {
                        themeFiles.Add((file.Path, stringContent));
                    }

                    if (!changedFiles.TryGetValue(type, out var entries))
                    {
                        entries = new List<GenericChangedEntry<FileEntity>>();
                        changedFiles.Add(type, entries);
                    }

                    entries.Add(CreateChangedEntry(file.Path, file.Type, EntryState.Modified));
                }
            }
        }

        private async Task RecordThemeFilesAsync(string storeId, List<(string Path, string Content)> themeFiles, bool draft, CancellationToken cancellationToken)
        {
            var record = ContentHistoryRecord.Create(draft ? ModuleConstants.GitHistory.SavedSummary : ModuleConstants.GitHistory.PublishedSummary, Request.GetOrCreateSaveId(), User);
            foreach (var (path, content) in themeFiles)
            {
                record.AddEntry(HistoryItem.ForThemeFile(path), content);
            }

            await contentHistory.RecordAsync(storeId, record, cancellationToken);
        }

        private static GenericChangedEntry<FileEntity> CreateChangedEntry(string path, string type, EntryState state)
        {
            var file = AbstractTypeFactory<FileEntity>.TryCreateInstance();
            file.Path = path;
            file.Type = type;

            return new GenericChangedEntry<FileEntity>(file, state);
        }

        private string GetContentBasePath(string storeId, string contentType, string theme)
        {
            var retVal = pathResolver.GetContentBasePath(contentType, storeId, theme);
            return retVal;
        }

        private async Task<string> GetCurrentThemeName(string storeId, string themeName) =>
            string.IsNullOrEmpty(themeName) ? await themeStorageFactory.GetThemeNameAsync(storeId) : themeName;

        public class SaveFilesModel
        {
            public string Files { get; set; }
        }

        public class SaveFileModel
        {
            public string Path { get; set; }
            public string Type { get; set; }
            public object Content { get; set; }
        }
    }
}
