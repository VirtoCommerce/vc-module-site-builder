using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.ContentModule.Core.Services;
using VirtoCommerce.ContentModule.Data.Extensions;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.Platform.Core.Common;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.ChangePreview;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Data.Services;

[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The services come from dependency injection; a parameter object would only hide them and change the constructor that subclasses call.")]
public class FilePageOperationService(
    IAuthorizationService authorizationService,
    IValidator<SiteBuilderContentRequest> requestValidator,
    IContentPermissionService contentPermissionService,
    IPublishingService publishingService,
    IGitContentPolicy gitContentPolicy,
    IGitContentRepository gitContentRepository,
    IGitContentHistory gitContentHistory,
    IGitContentPublisher gitContentPublisher,
    IGitLivePageService gitLivePageService,
    IGitHubConnectionService gitHubConnectionService,
    IOptions<GitContentOptions> gitContentOptions,
    IFilePageDraftService filePageDraftService,
    IThemeDraftService themeDraftService,
    IContentHistory contentHistory,
    ISiteBuilderContentGuard contentGuard) : IFilePageOperationService
{
    private const string Themes = "themes";
    // The content types the git flow covers are pages and blogs — the same .page files, blogs living
    // in a subfolder (see GitPageLocation.ContentPath). PagesContentType is the default assumed when a
    // caller sends no type at all.
    private const string PagesContentType = "pages";
    // The blob name of a draft in the flow that came before git: the page's name with this appended.
    private const string DraftSuffix = ModuleConstants.DraftSuffix;
    // How many "_N" names a duplicate will try before asking the caller to pick one. A page with
    // this many copies is a naming problem, not a paging problem.
    private const int MaxCopyIndex = 100;
    private const string OnlyCopyError = "Nothing is published: the draft is the only copy.";
    private const string DraftsInGitError = "The store's pages are in git: their drafts are discarded with git/discard-draft.";
    private const string MergeOutcomeWarning = "Whether the merge completes, waits for the checks or conflicts is known only when it runs.";

    public virtual Task<SiteBuilderOperationResult<FilePageShipment>> PublishAsync(FilePagePublishOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PublishInternalAsync(request, user, cancellationToken);
    }

    /// <summary>Ships the editor's own draft, never bytes a caller sends, and writes it to live storage of a repository the platform manages.</summary>
    private async Task<SiteBuilderOperationResult<FilePageShipment>> PublishInternalAsync(FilePagePublishOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var (storeId, type, path) = (request.StoreId, request.ContentType, request.Path);
        if (await CheckRequestAsync(PageRequest(storeId, type, path), request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<FilePageShipment>();
        }

        var storeRepository = await gitContentPolicy.GetRepositoryInfoAsync(storeId, cancellationToken);
        if (storeRepository == null)
        {
            return await PublishInBlobStorageAsync(request);
        }

        var location = GetDraftLocation(storeRepository.Name, request, user);

        var draft = await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, location.Branch, cancellationToken);
        if (draft == null)
        {
            return await RespondToNothingToPublishAsync(storeRepository, location, request, cancellationToken);
        }

        // The CI gate runs the full, schema-aware validator; this one only refuses to open a pull
        // request that could never pass it.
        var errors = PageEnvelopeValidator.Validate(ParsePage(draft));
        if (errors.Count > 0)
        {
            return SiteBuilderOperationResult.Invalid(GetPageErrorsMessage(path, errors), errors.ToList()).As<FilePageShipment>();
        }

        if (request.DryRun)
        {
            return DryRunShipment(await CreatePublishPreviewAsync(location, draft, request, cancellationToken));
        }

        return await MergeDraftAsync(storeRepository, location, draft, request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<FilePageShipment>> PublishInBlobStorageAsync(FilePagePublishOperationRequest request)
    {
        if (request.DryRun)
        {
            return DryRunShipment(CreateBlobPublishingPreview(request.Path, Draft, Published, "Publishes the draft in blob storage."));
        }

        // the switch is off for this store: publish means what it always meant
        await publishingService.PublishingAsync(request.ContentType ?? PagesContentType, request.StoreId, request.Path, publish: true);
        return Shipped(CreateShipment(GitPublishState.Merged, gitFlow: false), request);
    }

    private async Task<SiteBuilderOperationResult<FilePageShipment>> MergeDraftAsync(StoreRepository storeRepository, GitPageFileLocation location,
        string draft, FilePagePublishOperationRequest request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var (storeId, type, path) = (request.StoreId, request.ContentType, request.Path);
        // before the rebase, which moves the branch itself
        if (await CheckDraftHeadAsync(location, request.ExpectedDraftHead, cancellationToken) is { } moved)
        {
            return moved.As<FilePageShipment>();
        }

        if (request.Rebase)
        {
            await RebaseDraftAsync(location, draft, path, storeId, user, cancellationToken);
        }

        var result = await gitContentPublisher.MergeBranchAsync(location.Repository, location.Branch, $"publish {path} (store: {storeId})", cancellationToken);

        var shipment = await RespondToPublishAsync(result, location, path, canRebase: true, cancellationToken);
        if (storeRepository.IsPlatformManaged && result.State is GitPublishState.Merged or GitPublishState.AlreadyPublished)
        {
            await SyncPublishedLivePageAsync(storeId, type, path, location, cancellationToken);
        }

        return Shipped(shipment, request);
    }

    protected virtual async Task<SiteBuilderOperationResult<FilePageShipment>> RespondToNothingToPublishAsync(StoreRepository storeRepository,
        GitPageFileLocation location, FilePageShipOperationRequest request, CancellationToken cancellationToken)
    {
        await EnsureRepositoryReadableAsync(location.Repository, await GetBaseBranchAsync(cancellationToken), cancellationToken);

        // the draft reviewed is gone: a publish that only writes the live copy again is not what was approved
        if (!request.DryRun && await CheckDraftHeadAsync(location, request.ExpectedDraftHead, cancellationToken) is { } moved)
        {
            return moved.As<FilePageShipment>();
        }

        if (request.DryRun)
        {
            var preview = CreatePreview();
            preview.Warnings.Add("Nothing to publish: the editor has no draft of the page.");
            if (storeRepository.IsPlatformManaged)
            {
                preview.Warnings.Add("The live copy of the page is written again from what is published.");
            }

            return DryRunShipment(preview);
        }

        // nothing to merge; re-sync the live page anyway (repairs a failed or late live write)
        if (storeRepository.IsPlatformManaged)
        {
            await SyncPublishedLivePageAsync(request.StoreId, request.ContentType, request.Path, location, cancellationToken);
        }

        return Shipped(CreateShipment(GitPublishState.AlreadyPublished, gitFlow: true), request);
    }

    protected virtual async Task<SiteBuilderChangePreview> CreatePublishPreviewAsync(GitPageFileLocation location, string draft,
        FilePagePublishOperationRequest request, CancellationToken cancellationToken)
    {
        var baseBranch = await GetBaseBranchAsync(cancellationToken);
        var published = await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, baseBranch, cancellationToken);

        var preview = CreatePreview();
        var change = preview.AddObject(FilePage, request.Path)
            .AddField(Status, published == null ? Draft : Published, Published)
            .AddField(Content, published, draft);
        await AddDraftBranchAsync(change, location, cancellationToken);
        preview.Warnings.Add(request.Rebase
            ? $"Moves the draft branch {location.Branch} onto {baseBranch} and commits the draft on it, replacing what changed in {baseBranch} " +
              $"since the draft began, then merges the draft branch into {baseBranch}."
            : $"Merges the draft branch {location.Branch} into {baseBranch}.");
        preview.Warnings.Add(MergeOutcomeWarning);

        return preview;
    }

    protected virtual Task SyncPublishedLivePageAsync(string storeId, string type, string path, GitPageFileLocation location, CancellationToken cancellationToken) =>
        gitLivePageService.SyncLivePageAsync(storeId, type ?? PagesContentType, path, location, removeWhenGone: false, cancellationToken);

    /// <summary>
    /// The way out of a <see cref="GitPublishState.Conflict"/>: the draft, as a state, on top of today's
    /// base branch.
    /// <para>
    /// A page is one JSON document, so when the base branch has changed it since this branch was cut,
    /// git can rarely lay one edit over the other — and the work branch keeps its old merge base, so
    /// every retry reproduces the same conflict. Restoring a version does not help either: it commits
    /// onto the same branch. Before this, the only way out was someone closing the pull request and
    /// deleting the branch by hand.
    /// </para>
    /// <para>
    /// So the branch is moved to the base branch and the draft's bytes become one commit on it — the
    /// same idea promotion is built on: what is wanted is the file's state, not its history. The other
    /// edits are replaced by this one; they stay in the base branch's history, and the old branch's
    /// commits stay reachable by sha. The open pull request is kept and recomputed against the moved
    /// branch rather than closed and opened again. Only on request — the editor was shown what they
    /// are replacing — never as a default: silently taking either side would throw away someone's work.
    /// </para>
    /// <para>
    /// Moved, not deleted and cut again. Delete-then-create leaves a moment with no branch at all, and
    /// a failure in that moment leaves the draft as a commit nothing points at — while the next publish
    /// reads no draft and reports the page as already published, which is the one answer this flow must
    /// never give wrongly. It also cannot be done reliably: <see cref="IGitContentRepository.CreateBranchAsync"/>
    /// treats an existing ref as success without moving it, and GitHub can still report the deleted ref
    /// for a moment, so the branch would keep its old merge base and meet the same conflict again.
    /// </para>
    /// </summary>
    protected virtual async Task RebaseDraftAsync(GitPageFileLocation location, string draft, string path, string storeId, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var baseBranch = await GetBaseBranchAsync(cancellationToken);

        var baseSha = await gitContentRepository.GetBranchHeadShaAsync(location.Repository, baseBranch, cancellationToken)
            ?? throw new InvalidOperationException($"Could not resolve \"{baseBranch}\" to a commit.");

        // Where the draft is now, so it can be put back if what follows does not finish.
        var draftHead = await gitContentRepository.GetBranchHeadShaAsync(location.Repository, location.Branch, cancellationToken);

        try
        {
            await gitContentRepository.SetBranchAsync(location.Repository, location.Branch, baseSha, location.RepoPath, cancellationToken);

            var author = GetCurrentAuthor(user);
            var message = GetCommitMessage($"designer: publish {path} on top of {baseBranch}, replacing what changed there since this draft began (store: {storeId}, by: {author.Name})", user);
            await gitContentRepository.CommitFileAsync(location.Repository, location.RepoPath, draft, location.Branch, message, author, cancellationToken);
        }
        catch (Exception failure) when (draftHead != null)
        {
            try
            {
                // Deliberately not the request's token. What is being undone here includes the request
                // being cancelled — the editor closing the tab while the commit was in flight — and
                // passing the token that has just been cancelled would mean the undo cannot run in
                // exactly the case it exists for, leaving the branch on the base commit with the draft
                // reachable only by a sha nobody is left to read. The call is bounded by the HTTP
                // client's own timeout.
                await gitContentRepository.SetBranchAsync(location.Repository, location.Branch, draftHead, location.RepoPath, CancellationToken.None);
            }
            catch (Exception restoreFailure)
            {
                // Both the rebuild and the undo failed. The draft is still a commit in the repository
                // and nothing but its sha can find it now, so the sha goes in the message.
                throw new InvalidOperationException(
                    $"Publishing \"{path}\" on top of {baseBranch} failed, and moving the draft branch back failed too. " +
                    $"The draft is commit {draftHead}; it can be restored with: git branch {location.Branch} {draftHead}",
                    new AggregateException(failure, restoreFailure));
            }

            throw;
        }
        finally
        {
            gitContentHistory.Invalidate(location.Repository, location.RepoPath);
        }
    }

    public virtual Task<SiteBuilderOperationResult<FilePageShipment>> UnpublishAsync(FilePageShipOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UnpublishInternalAsync(request, user, cancellationToken);
    }

    /// <summary>Removes the page from live storage of a repository the platform manages only once the removal is merged, so a revert stays a rollback.</summary>
    private async Task<SiteBuilderOperationResult<FilePageShipment>> UnpublishInternalAsync(FilePageShipOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var (storeId, type, path) = (request.StoreId, request.ContentType, request.Path);
        if (await CheckRequestAsync(PageRequest(storeId, type, path), request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<FilePageShipment>();
        }

        var storeRepository = await gitContentPolicy.GetRepositoryInfoAsync(storeId, cancellationToken);
        if (storeRepository == null)
        {
            return await UnpublishInBlobStorageAsync(request);
        }

        var baseBranch = await GetBaseBranchAsync(cancellationToken);
        var location = GetDraftLocation(storeRepository.Name, request, user);

        var published = await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, baseBranch, cancellationToken);
        if (published == null)
        {
            return await RespondToNothingToUnpublishAsync(storeRepository, location, baseBranch, request, cancellationToken);
        }

        if (request.DryRun)
        {
            return DryRunShipment(await CreateUnpublishPreviewAsync(location, baseBranch, path, published, cancellationToken));
        }

        // before the deletion is committed, which moves the branch itself
        if (await CheckDraftHeadAsync(location, request.ExpectedDraftHead, cancellationToken) is { } moved)
        {
            return moved.As<FilePageShipment>();
        }

        await CommitUnpublishAsync(location, baseBranch, storeId, path, user, cancellationToken);

        var result = await gitContentPublisher.MergeBranchAsync(location.Repository, location.Branch, $"unpublish {path} (store: {storeId})", cancellationToken);

        var shipment = await RespondToPublishAsync(result, location, path, canRebase: false, cancellationToken);
        if (result.State == GitPublishState.Merged && storeRepository.IsPlatformManaged)
        {
            await SyncUnpublishedLivePageAsync(storeId, type, path, location, cancellationToken);
        }

        return Shipped(shipment, request);
    }

    private async Task<SiteBuilderOperationResult<FilePageShipment>> UnpublishInBlobStorageAsync(FilePageOperationRequest request)
    {
        if (request.DryRun)
        {
            return DryRunShipment(CreateBlobPublishingPreview(request.Path, Published, Draft, "Unpublishes the page in blob storage."));
        }

        await publishingService.PublishingAsync(request.ContentType ?? PagesContentType, request.StoreId, request.Path, publish: false);
        return Shipped(CreateShipment(GitPublishState.Merged, gitFlow: false), request);
    }

    protected virtual async Task<SiteBuilderChangePreview> CreateUnpublishPreviewAsync(GitPageFileLocation location, string baseBranch, string path,
        string published, CancellationToken cancellationToken)
    {
        var preview = CreatePreview();
        var change = preview.AddObject(FilePage, path).AddField(Status, Published, Draft).AddField(Content, published, null);
        await AddDraftBranchAsync(change, location, cancellationToken);
        preview.Warnings.Add($"Deletes the page on the draft branch {location.Branch} and merges the draft branch into {baseBranch}.");
        preview.Warnings.Add(MergeOutcomeWarning);

        return preview;
    }

    protected virtual async Task<SiteBuilderOperationResult<FilePageShipment>> RespondToNothingToUnpublishAsync(StoreRepository storeRepository,
        GitPageFileLocation location, string baseBranch, FilePageOperationRequest request, CancellationToken cancellationToken)
    {
        await EnsureRepositoryReadableAsync(location.Repository, baseBranch, cancellationToken);

        // retry of a merged unpublish whose live removal failed; a page the branch never had stays live
        if (storeRepository.IsPlatformManaged && await gitLivePageService.IsRemovedFromBaseBranchAsync(location, cancellationToken))
        {
            if (request.DryRun)
            {
                var preview = CreatePreview();
                preview.Warnings.Add($"The page is already removed from {baseBranch}: only its live copy is removed.");
                return DryRunShipment(preview);
            }

            await SyncUnpublishedLivePageAsync(request.StoreId, request.ContentType, request.Path, location, cancellationToken);
            return Shipped(CreateShipment(GitPublishState.AlreadyPublished, gitFlow: true), request);
        }

        return SiteBuilderOperationResult.NotFound(AbstractTypeFactory<FilePageShipment>.TryCreateInstance());
    }

    protected virtual async Task CommitUnpublishAsync(GitPageFileLocation location, string baseBranch, string storeId, string path, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var hadBranch = await gitContentRepository.GetBranchHeadShaAsync(location.Repository, location.Branch, cancellationToken) != null;
        if (!hadBranch)
        {
            await gitContentRepository.CreateBranchAsync(location.Repository, location.Branch, baseBranch, cancellationToken);
        }

        // A branch cut from the production branch a moment ago still holds the page. One that was
        // already there may not: an earlier unpublish whose merge is waiting to be asked for again
        // has committed the deletion, and all that is left of this operation is the merge. Deleting
        // a file that is already gone is an error rather than a no-op, so that retry would fail here
        // instead of finishing the unpublish it is retrying.
        if (!hadBranch || await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, location.Branch, cancellationToken) != null)
        {
            await gitContentRepository.DeleteFileAsync(location.Repository, location.RepoPath, location.Branch,
                GetCommitMessage($"unpublish {path} (store: {storeId})", user), GetCurrentAuthor(user), cancellationToken);
            gitContentHistory.Invalidate(location.Repository, location.RepoPath);
        }
    }

    protected virtual Task SyncUnpublishedLivePageAsync(string storeId, string type, string path, GitPageFileLocation location, CancellationToken cancellationToken) =>
        gitLivePageService.SyncLivePageAsync(storeId, type ?? PagesContentType, path, location, removeWhenGone: true, cancellationToken);

    public virtual Task<SiteBuilderOperationResult<FilePageShipment>> PromoteAsync(FilePageOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PromoteInternalAsync(request, user, cancellationToken);
    }

    /// <summary>
    /// Promotes a page to production: the state of its file on <c>BaseBranch</c>, placed onto
    /// <c>ReleaseBranch</c> as its own commit.
    /// <para>
    /// Per page, and never a merge of one shared branch into the other. Merging would ship everything
    /// else the base branch happens to be holding, which is exactly what an editor promoting one page
    /// does not mean to do. Moving the file's STATE rather than replaying commits also means a page
    /// edited several times since the last promotion needs no history to line up: production ends up
    /// with what dev has now, which is the only thing ever wanted here.
    /// </para>
    /// </summary>
    private async Task<SiteBuilderOperationResult<FilePageShipment>> PromoteInternalAsync(FilePageOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var (storeId, type, path) = (request.StoreId, request.ContentType, request.Path);
        if (await CheckRequestAsync(PageRequest(storeId, type, path), request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<FilePageShipment>();
        }

        var repository = await gitContentPolicy.GetRepositoryAsync(storeId, cancellationToken);
        if (repository == null)
        {
            // without git there is no second branch to promote into
            return SiteBuilderOperationResult.NotFound().As<FilePageShipment>();
        }

        var options = gitContentOptions.Value;
        var baseBranch = await GetBaseBranchAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(options.ReleaseBranch))
        {
            // A configuration answer, not a missing page: this installation has no production contour,
            // and saying so is more useful than a 404 that reads as "no such page".
            return SiteBuilderOperationResult.Invalid(
                $"{GitContentOptions.SectionName}:{nameof(GitContentOptions.ReleaseBranch)} is not set: " +
                "this installation has no production branch to promote into.").As<FilePageShipment>();
        }

        var location = GetLocation(repository, type, path, user);

        // A promotion decides on the branches as they are now, so an edit made on GitHub a moment ago counts.
        // A dry run changes nothing, the read cache included: it reads the branches as they are cached.
        if (!request.DryRun)
        {
            gitContentRepository.InvalidateRead(location.Repository, location.RepoPath, baseBranch);
            gitContentRepository.InvalidateRead(location.Repository, location.RepoPath, options.ReleaseBranch);
        }

        var onBase = await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, baseBranch, cancellationToken);
        if (onBase == null)
        {
            return SiteBuilderOperationResult.Invalid(
                $"\"{path}\" is not on {baseBranch}. Publish it there first — production " +
                "follows what has already been through the dev environment.").As<FilePageShipment>();
        }

        var onRelease = await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, options.ReleaseBranch, cancellationToken);
        var alreadyPromoted = onRelease != null && PageJson.AreSame(onBase, onRelease);
        var branch = options.GetPromoteBranch(type, path);
        if (request.DryRun)
        {
            var preview = CreatePreview();
            if (alreadyPromoted)
            {
                preview.Warnings.Add($"Nothing to promote: {options.ReleaseBranch} already holds the page as it is on {baseBranch}.");
                return DryRunShipment(preview);
            }

            preview.AddObject(FilePage, path).AddField(Content, onRelease, onBase);
            preview.Warnings.Add($"Commits the page as it is on {baseBranch} to {branch} and merges that into {options.ReleaseBranch}.");
            preview.Warnings.Add(MergeOutcomeWarning);
            return DryRunShipment(preview);
        }

        if (alreadyPromoted)
        {
            // production already holds this exact page; opening a pull request would only produce an
            // empty one, and the editor would be left waiting for a merge that means nothing
            return Shipped(CreateShipment(GitPublishState.AlreadyPublished, gitFlow: true), request);
        }

        await PreparePromoteBranchAsync(location.Repository, branch, location.RepoPath, options.ReleaseBranch, cancellationToken);

        // The bytes come from the base branch, never from the request: promotion ships what the dev
        // environment has been serving, not what a client happens to send along with the button press.
        await gitContentRepository.CommitFileAsync(
            location.Repository,
            location.RepoPath,
            onBase,
            branch,
            GetCommitMessage($"promote {path} to {options.ReleaseBranch} (store: {storeId})", user),
            GetCurrentAuthor(user),
            cancellationToken);

        var result = await gitContentPublisher.MergeBranchIntoAsync(
            location.Repository, branch, $"promote {path} (store: {storeId})", options.ReleaseBranch, cancellationToken);

        return Shipped(await RespondToShipAsync(result, location.Repository, location.RepoPath, branch, options.ReleaseBranch, path, canRebase: false,
            cancellationToken), request);
    }

    /// <summary>
    /// Makes the promote branch ready to commit this promotion to.
    /// <para>
    /// It is reused only while its pull request is still open — that is what makes two editors
    /// promoting the same page land on one pull request rather than two. Once that pull request is
    /// gone the branch has to be cut again, because it can outlive it: the merge is a squash, so the
    /// branch's head is not on the production branch's history afterwards, and GitHub deletes a
    /// merged head only where the repository is configured to. Committing onto the leftover would
    /// make the promotion a three-way merge of two full rewrites of the same file — a conflict every
    /// retry reproduces, since the stale merge base never moves.
    /// </para>
    /// </summary>
    protected virtual async Task PreparePromoteBranchAsync(string repository, string branch, string repoPath, string releaseBranch,
        CancellationToken cancellationToken)
    {
        var leftover = await gitContentRepository.GetBranchHeadShaAsync(repository, branch, cancellationToken);
        if (leftover != null &&
            await gitContentPublisher.GetOpenPullRequestAsync(repository, branch, cancellationToken) != null)
        {
            return;
        }

        if (leftover != null)
        {
            await gitContentRepository.DeleteBranchAsync(repository, branch, repoPath, cancellationToken);
        }

        await gitContentRepository.CreateBranchAsync(repository, branch, releaseBranch, cancellationToken);
    }

    public virtual Task<SiteBuilderOperationResult<FilePageVersionRestore>> RestoreVersionAsync(FilePageRestoreVersionOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RestoreVersionInternalAsync(request, user, cancellationToken);
    }

    /// <summary>
    /// Continues editing from an earlier version: the content of the commit becomes a new commit on this
    /// editor's own work branch.
    /// <para>
    /// Forward only, and that is the whole design. Writing into the branch the version came from would
    /// mean force-pushing over whatever came after it — destroying the audit trail the version list
    /// exists to provide — or writing into a branch that belongs to another editor, which is the single
    /// global draft slot this flow removed. Appending instead keeps history append-only, leaves branch
    /// ownership at one editor per page, and gives a rollback that needs no git: restore, then publish.
    /// </para>
    /// </summary>
    private async Task<SiteBuilderOperationResult<FilePageVersionRestore>> RestoreVersionInternalAsync(FilePageRestoreVersionOperationRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var (storeId, path, sha) = (request.StoreId, request.Path, request.Sha);
        var contentType = request.ContentType.IsNullOrEmpty() ? PagesContentType : request.ContentType;
        var contentRequest = PageRequest(storeId, contentType, path);
        // a version is a commit; a branch name would mean "whatever is there when this runs"
        contentRequest.Ref = sha;
        if (await CheckRequestAsync(contentRequest, request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<FilePageVersionRestore>();
        }

        var repository = await gitContentPolicy.GetRepositoryAsync(storeId, cancellationToken);
        if (repository == null)
        {
            // without git there are no versions to continue from
            return SiteBuilderOperationResult.NotFound().As<FilePageVersionRestore>();
        }

        var baseBranch = await GetBaseBranchAsync(cancellationToken);
        var location = GetLocation(repository, contentType, path, user);

        var content = await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, sha, cancellationToken);
        if (content == null)
        {
            // no such commit, or the page did not exist in it
            return SiteBuilderOperationResult.NotFound(AbstractTypeFactory<FilePageVersionRestore>.TryCreateInstance());
        }

        // The same gate a save passes: restoring a version that CI would refuse to deploy would leave
        // the editor with a branch that cannot be published.
        var page = ParsePage(content);
        var errors = PageEnvelopeValidator.Validate(page);
        if (errors.Count == 0)
        {
            // an old version may predate the checks: it is saved again sanitized, or not at all
            errors = contentGuard.GuardPage(page, path, catalog: null).Errors;
        }

        if (errors.Count > 0)
        {
            return SiteBuilderOperationResult.Invalid(GetPageErrorsMessage(path, errors), errors.ToList()).As<FilePageVersionRestore>();
        }

        if (request.DryRun)
        {
            var current = await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, location.Branch, cancellationToken)
                ?? await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, baseBranch, cancellationToken);
            var preview = CreatePreview();
            preview.AddObject(FilePage, path).AddField(Content, current, PageJson.Serialize(page));
            preview.Warnings.Add($"Commits the version {sha} to the draft branch {location.Branch}.");
            return SiteBuilderOperationResult.DryRun<FilePageVersionRestore>(preview);
        }

        if (await gitContentRepository.GetBranchHeadShaAsync(location.Repository, location.Branch, cancellationToken) == null)
        {
            await gitContentRepository.CreateBranchAsync(location.Repository, location.Branch, baseBranch, cancellationToken);
        }

        var author = GetCurrentAuthor(user);
        var shortSha = sha[..7];
        var message = GetCommitMessage($"designer: restore {path} from {shortSha} (store: {storeId}, by: {author.Name})", user);

        // Canonical bytes, not the ones read from the commit: an older version may have been written
        // before the canonical form existed, and re-committing it verbatim would make publish-status
        // report changes that are only line endings.
        var commitSha = await gitContentRepository.CommitFileAsync(location.Repository, location.RepoPath, PageJson.Serialize(page), location.Branch, message,
            author, cancellationToken);
        gitContentHistory.Invalidate(location.Repository, location.RepoPath);

        var result = AbstractTypeFactory<FilePageVersionRestore>.TryCreateInstance();
        result.Branch = location.Branch;
        result.CommitSha = commitSha;
        result.RestoredFrom = sha;

        return SiteBuilderOperationResult.Success(result);
    }

    public virtual Task<SiteBuilderOperationResult<GitPageCommit>> CopyAsync(FilePageCopyOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return CopyInternalAsync(request, user, cancellationToken);
    }

    /// <summary>
    /// Duplicates a page inside the content repository: a commit of the same document under a new
    /// name, on the copying editor's work branch. Unpublished means "on a branch, not on the base
    /// branch" here, so that is what a copy has to be.
    /// </summary>
    private async Task<SiteBuilderOperationResult<GitPageCommit>> CopyInternalAsync(FilePageCopyOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var (storeId, srcPath, destPath) = (request.StoreId, request.Path, request.TargetPath);
        // A target the server picks needs no check of its own: it keeps the source's folder and extension.
        var contentType = request.ContentType ?? PagesContentType;
        string[] pages = destPath.IsNullOrEmpty() ? [srcPath] : [srcPath, destPath];
        if (await CheckRequestAsync(PageRequest(storeId, contentType, pages), request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<GitPageCommit>();
        }

        var repository = await gitContentPolicy.GetRepositoryAsync(storeId, cancellationToken);
        if (repository == null)
        {
            return SiteBuilderOperationResult.NotFound().As<GitPageCommit>();
        }

        // What the editor sees, which is their own draft of the source when they have one. Copying
        // the published version instead would silently duplicate something other than what is on
        // screen.
        var source = await ReadDraftFromGitAsync(GetLocation(repository, contentType, srcPath, user), cancellationToken);
        if (source == null)
        {
            return RefuseSource(srcPath, $"\"{srcPath}\" is not in the content repository.");
        }

        var target = destPath.IsNullOrEmpty() ? await NextFreeCopyPathAsync(repository, contentType, srcPath, user, cancellationToken) : destPath;
        if (target == null)
        {
            return RefuseSource(srcPath, $"\"{srcPath}\" already has {MaxCopyIndex} copies; name the next one yourself.");
        }

        var page = ParsePage(source);
        if (page.Type == JTokenType.Null)
        {
            return RefuseSource(srcPath, $"\"{srcPath}\" is not a page document.");
        }

        if (request.DryRun)
        {
            var preview = CreatePreview();
            preview.AddObject(FilePage, target).AddField(Content, null, PageJson.Serialize(page));
            preview.Warnings.Add($"Commits a copy of \"{srcPath}\" as \"{target}\" to the editor's draft branch; the copy is not published.");
            return SiteBuilderOperationResult.DryRun<GitPageCommit>(preview);
        }

        var saved = await filePageDraftService.CommitDraftAsync(
            storeId,
            repository,
            new SiteBuilderContentFile { Path = target, ContentType = contentType, Content = page },
            $"copy {WithoutDraftSuffix(srcPath)} to",
            user,
            cancellationToken);

        return SiteBuilderOperationResult.Success(saved);
    }

    protected virtual SiteBuilderOperationResult<GitPageCommit> RefuseSource(string srcPath, string error)
    {
        var source = AbstractTypeFactory<GitPageCommit>.TryCreateInstance();
        source.Path = srcPath;

        return SiteBuilderOperationResult.Invalid(source, error);
    }

    /// <summary>
    /// The name a duplicate gets: "foo.page" becomes "foo_1.page", at the first index the repository
    /// does not already hold — or <c>null</c> when there is no free one within reach.
    /// <para>
    /// Deliberately the same shape the blob flow produces, so a store that switched flows goes on
    /// getting the names its editors are used to. The language segment is kept where the file has
    /// one ("foo.de.page" becomes "foo_1.de.page"), because it is part of which page this is.
    /// </para>
    /// </summary>
    protected virtual async Task<string> NextFreeCopyPathAsync(string repository, string contentType, string srcPath, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var path = WithoutDraftSuffix(srcPath);
        var extension = Path.GetExtension(path);
        var fileName = Path.GetFileNameWithoutExtension(path);
        var name = path.GetFileNameWithoutLanguage();
        var language = path.GetLanguage();
        var languageSuffix = language.IsNullOrEmpty() ? string.Empty : $".{language}";
        var folder = path[..^(fileName.Length + extension.Length)];

        for (var index = 1; index <= MaxCopyIndex; index++)
        {
            var candidate = $"{folder}{name}_{index}{languageSuffix}{extension}";
            if (!await PageExistsInRepositoryAsync(repository, contentType, candidate, user, cancellationToken))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether the repository holds this page anywhere that matters for naming: on the base branch,
    /// or on this editor's work branch. Leaving the branch out would hand the same name to two
    /// duplicates in a row, because a copy that has not been published lives only there.
    /// </summary>
    protected virtual async Task<bool> PageExistsInRepositoryAsync(string repository, string contentType, string path, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var location = GetLocation(repository, contentType, path, user);

        return await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, await GetBaseBranchAsync(cancellationToken), cancellationToken) != null
            || await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, location.Branch, cancellationToken) != null;
    }

    /// <summary>The page as the editor edits it: their draft, else what is published; <c>null</c> when git does not hold it.</summary>
    protected virtual async Task<string> ReadDraftFromGitAsync(GitPageFileLocation location, CancellationToken cancellationToken) =>
        await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, location.Branch, cancellationToken)
        ?? await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, await GetBaseBranchAsync(cancellationToken), cancellationToken);

    public virtual Task<SiteBuilderOperationResult> DiscardGitDraftAsync(FilePageDraftOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return DiscardGitDraftInternalAsync(request, user, cancellationToken);
    }

    /// <summary>
    /// Throws this editor's draft of the page away, so the next read starts from what is published.
    /// The way out of a conflict, and the only one that does not quietly pick a winner.
    /// </summary>
    private async Task<SiteBuilderOperationResult> DiscardGitDraftInternalAsync(FilePageDraftOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var (storeId, type, path) = (request.StoreId, request.ContentType, request.Path);
        if (await CheckRequestAsync(PageRequest(storeId, type, path), request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused;
        }

        var repository = await gitContentPolicy.GetRepositoryAsync(storeId, cancellationToken);
        if (repository == null)
        {
            return SiteBuilderOperationResult.NotFound();
        }

        var location = GetLocation(repository, type, path, user);
        if (request.DryRun)
        {
            return SiteBuilderOperationResult.DryRun(await CreateDiscardGitDraftPreviewAsync(location, path, cancellationToken));
        }

        if (await CheckDraftHeadAsync(location, request.ExpectedDraftHead, cancellationToken) is { } moved)
        {
            return moved;
        }

        await filePageDraftService.DiscardGitDraftAsync(repository, type, path, user, cancellationToken);

        return SiteBuilderOperationResult.Success();
    }

    protected virtual async Task<SiteBuilderChangePreview> CreateDiscardGitDraftPreviewAsync(GitPageFileLocation location, string path,
        CancellationToken cancellationToken)
    {
        var draft = await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, location.Branch, cancellationToken);
        var preview = CreatePreview();
        if (draft != null)
        {
            var published = await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, await GetBaseBranchAsync(cancellationToken),
                cancellationToken);
            await AddDraftBranchAsync(preview.AddObject(FilePage, path).AddField(Content, draft, published), location, cancellationToken);
        }

        preview.Warnings.Add($"Deletes the draft branch {location.Branch}.");

        return preview;
    }

    public virtual Task<SiteBuilderOperationResult<FileDraftDiscard>> DiscardDraftAsync(FileDraftDiscardOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return DiscardDraftInternalAsync(request, user, cancellationToken);
    }

    /// <summary>Refused when nothing is published, as the draft is then the only copy.</summary>
    private async Task<SiteBuilderOperationResult<FileDraftDiscard>> DiscardDraftInternalAsync(FileDraftDiscardOperationRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var (storeId, path, theme) = (request.StoreId, request.Path, request.Theme);
        var contentType = string.IsNullOrEmpty(request.ContentType) ? PagesContentType : request.ContentType.ToLowerInvariant();
        var contentRequest = new SiteBuilderContentRequest { StoreId = storeId, ContentType = contentType, Theme = theme, Paths = [path] };
        if (await CheckRequestAsync(contentRequest, request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<FileDraftDiscard>();
        }

        if (await CheckDiscardAsync(storeId, contentType, path, user, cancellationToken) is { } refusedDiscard)
        {
            return refusedDiscard;
        }

        if (request.DryRun)
        {
            return await PreviewDiscardAsync(storeId, contentType, path, theme, cancellationToken);
        }

        if (contentType != Themes)
        {
            return await DiscardBlobDraftAsync(request, contentType, cancellationToken);
        }

        try
        {
            return await DiscardThemeDraftAsync(storeId, path, request.SaveId, request.ExpectedDraftVersion, user, cancellationToken);
        }
        catch (SiteBuilderContentConflictException ex)
        {
            return SiteBuilderOperationResult.Conflict(ex.Message).As<FileDraftDiscard>();
        }
    }

    /// <summary>Blob storage holds no lock over a draft: its version is read right before the discard.</summary>
    protected virtual async Task<SiteBuilderOperationResult<FileDraftDiscard>> DiscardBlobDraftAsync(FileDraftDiscardOperationRequest request, string contentType,
        CancellationToken cancellationToken)
    {
        var (storeId, path, theme) = (request.StoreId, request.Path, request.Theme);
        if (request.ExpectedDraftVersion != null)
        {
            var draft = await filePageDraftService.PreviewDiscardBlobDraftAsync(storeId, contentType, path, theme, cancellationToken);
            if (!string.Equals(GetDiscardVersion(storeId, contentType, path, draft), request.ExpectedDraftVersion.Trim(), StringComparison.Ordinal))
            {
                return SiteBuilderOperationResult.Conflict(SiteBuilderContentConflictException.DraftChangedMessage).As<FileDraftDiscard>();
            }
        }

        var status = await filePageDraftService.DiscardBlobDraftAsync(storeId, contentType, path, theme, cancellationToken);
        return status == DraftDiscardStatus.OnlyCopy ? RefuseOnlyCopy() : Discarded(status);
    }

    /// <summary>The permission to edit a theme file; a page kept in git has its draft discarded in git instead.</summary>
    private async Task<SiteBuilderOperationResult<FileDraftDiscard>> CheckDiscardAsync(string storeId, string contentType, string path, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (contentType == Themes && !await contentPermissionService.HasPermissionsAsync(user, [contentPermissionService.GetThemeFileEditPermission(path)]))
        {
            return SiteBuilderOperationResult.Forbidden().As<FileDraftDiscard>();
        }

        if (GitPageLocation.IsPageContent(contentType) && await gitContentPolicy.IsEnabledForStoreAsync(storeId, cancellationToken))
        {
            var keptInGit = AbstractTypeFactory<FileDraftDiscard>.TryCreateInstance();
            keptInGit.KeptInGit = true;
            return SiteBuilderOperationResult.Invalid(keptInGit, DraftsInGitError);
        }

        return null;
    }

    private async Task<SiteBuilderOperationResult<FileDraftDiscard>> PreviewDiscardAsync(string storeId, string contentType, string path, string theme,
        CancellationToken cancellationToken)
    {
        var isThemeFile = contentType == Themes;
        var draft = isThemeFile
            ? await themeDraftService.PreviewDiscardAsync(storeId, path, cancellationToken)
            : await filePageDraftService.PreviewDiscardBlobDraftAsync(storeId, contentType, path, theme, cancellationToken);
        if (draft.Status == DraftDiscardStatus.OnlyCopy)
        {
            return RefuseOnlyCopy();
        }

        var eTag = GetDiscardVersion(storeId, contentType, path, draft);
        return SiteBuilderOperationResult.DryRun<FileDraftDiscard>(CreateDiscardPreview(isThemeFile ? ThemeFile : FilePage, path, eTag, draft));
    }

    /// <param name="eTag">The version the draft has to be at; <c>null</c> discards it whatever it holds.</param>
    /// <exception cref="SiteBuilderContentConflictException">The draft is at another version, or gone: nothing is discarded.</exception>
    protected virtual async Task<SiteBuilderOperationResult<FileDraftDiscard>> DiscardThemeDraftAsync(string storeId, string path, string saveId, string eTag,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var eTags = eTag == null ? null : new Dictionary<string, string> { [path] = eTag };
        var draft = (await themeDraftService.DiscardDraftsAsync(storeId, [path], eTags, _ => Task.CompletedTask, cancellationToken)).Single();
        if (draft.Status == DraftDiscardStatus.OnlyCopy)
        {
            return RefuseOnlyCopy();
        }

        if (draft.Status == DraftDiscardStatus.Discarded)
        {
            var record = ContentHistoryRecord.Create(ModuleConstants.GitHistory.DiscardedSummary, saveId, user);
            record.AddEntry(HistoryItem.ForThemeFile(path), draft.PublishedContent);
            await contentHistory.RecordAsync(storeId, record, cancellationToken);
        }

        return Discarded(draft.Status);
    }

    /// <param name="eTag">The version of the draft, which the caller sends back to discard only what it reviewed.</param>
    protected virtual SiteBuilderChangePreview CreateDiscardPreview(string objectType, string path, string eTag, DraftDiscardPreview draft)
    {
        var preview = CreatePreview();
        if (draft.Status == DraftDiscardStatus.Discarded)
        {
            preview.AddObject(objectType, path, eTag).AddField(Content, draft.DraftContent, draft.PublishedContent);
        }
        else
        {
            preview.Warnings.Add($"\"{path}\" has no draft: nothing is discarded.");
        }

        return preview;
    }

    /// <summary>The version of a draft a discard deletes; <c>null</c> when it deletes none.</summary>
    protected virtual string GetDiscardVersion(string storeId, string contentType, string path, DraftDiscardPreview draft)
    {
        if (draft.Status != DraftDiscardStatus.Discarded)
        {
            return null;
        }

        return contentType == Themes
            ? SiteBuilderContentVersion.CreateForThemeFile(storeId, path, draft.DraftContent)
            : SiteBuilderContentVersion.CreateForFilePage(storeId, contentType, path, draft.DraftContent);
    }

    protected virtual SiteBuilderOperationResult<FileDraftDiscard> RefuseOnlyCopy()
    {
        var onlyCopy = AbstractTypeFactory<FileDraftDiscard>.TryCreateInstance();
        onlyCopy.Status = DraftDiscardStatus.OnlyCopy;

        return SiteBuilderOperationResult.Invalid(onlyCopy, OnlyCopyError);
    }

    protected virtual SiteBuilderOperationResult<FileDraftDiscard> Discarded(DraftDiscardStatus status)
    {
        var result = AbstractTypeFactory<FileDraftDiscard>.TryCreateInstance();
        result.Status = status;

        return SiteBuilderOperationResult.Success(result);
    }

    /// <summary>Store access first, so a user bound to another store cannot tell from a refused request which stores exist.</summary>
    protected virtual async Task<SiteBuilderOperationResult> CheckRequestAsync(SiteBuilderContentRequest request, IList<string> bindingErrors,
        ClaimsPrincipal user, CancellationToken cancellationToken) =>
        await authorizationService.CheckStoreOperationAsync(user, request)
        ?? await requestValidator.CheckOperationRequestAsync(request, bindingErrors, cancellationToken);

    protected virtual async Task<FilePageShipment> RespondToPublishAsync(GitPublishResult result, GitPageFileLocation location, string path, bool canRebase,
        CancellationToken cancellationToken) =>
        await RespondToShipAsync(result, location.Repository, location.RepoPath, location.Branch, await GetBaseBranchAsync(cancellationToken), path, canRebase,
            cancellationToken);

    /// <summary>
    /// The tail every shipping operation shares — publish, unpublish and promote differ in which
    /// branch they merged into, and in nothing else once the merge has happened.
    /// <para>
    /// <paramref name="canRebase"/> is whether a conflict has a way out from the builder: publishing
    /// the draft again with a rebase (see <see cref="RebaseDraftAsync"/>). Only a publish offers it;
    /// an unpublish or a promotion that conflicts is a state for a person to look at.
    /// </para>
    /// </summary>
    protected virtual async Task<FilePageShipment> RespondToShipAsync(GitPublishResult result, string repository, string repoPath, string branch,
        string mergedInto, string path, bool canRebase, CancellationToken cancellationToken)
    {
        if (result.State == GitPublishState.Merged)
        {
            await gitContentRepository.DeleteBranchAsync(repository, branch, repoPath, cancellationToken);

            // the merge moved that branch, so what it said about this page a moment ago is the
            // pre-merge content — status would report the page as still unshipped
            gitContentRepository.InvalidateRead(repository, repoPath, mergedInto);
            // and the draft version that just shipped is now a published one
            gitContentHistory.Invalidate(repository, repoPath);
        }

        var shipment = CreateShipment(result.State, gitFlow: true);
        shipment.Merge = result;
        shipment.CanRebase = canRebase;
        if (result.State == GitPublishState.Conflict)
        {
            var wayOut = canRebase
                ? " Re-read the page and apply the change again, or publish this draft as it is on top of the current page — that replaces what changed there."
                : " Re-read the page and apply the change again.";
            shipment.ConflictError = $"\"{path}\" changed in {mergedInto} while this draft was being written, and the two edits cannot be merged.{wayOut}";
        }

        return shipment;
    }

    /// <summary>
    /// GitHub answers a repository that does not exist, or that the platform cannot see, the way it
    /// answers a file that is not there, so a missing file means nothing about the page until the
    /// repository itself answers.
    /// </summary>
    /// <exception cref="GitContentException">The repository does not answer.</exception>
    protected virtual async Task EnsureRepositoryReadableAsync(string repository, string baseBranch, CancellationToken cancellationToken)
    {
        if (await gitContentRepository.GetBranchHeadShaAsync(repository, baseBranch, cancellationToken) == null)
        {
            throw GitContentException.RepositoryUnreachable(repository);
        }
    }

    /// <summary>The draft branch has to be at the commit the caller reviewed; <c>null</c> takes it wherever it is.</summary>
    /// <returns>The conflict when the branch has moved or is gone; <c>null</c> otherwise.</returns>
    protected virtual async Task<SiteBuilderOperationResult> CheckDraftHeadAsync(GitPageFileLocation location, string expectedDraftHead,
        CancellationToken cancellationToken)
    {
        if (expectedDraftHead == null)
        {
            return null;
        }

        // GitHub merges a branch wherever it is when the merge runs, so this is read as late as the operation can
        var head = await gitContentRepository.GetBranchHeadShaAsync(location.Repository, location.Branch, cancellationToken);

        return string.Equals(head, expectedDraftHead.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : SiteBuilderOperationResult.Conflict(SiteBuilderContentConflictException.DraftBranchMovedMessage);
    }

    /// <summary>The branch a write takes the draft from and the commit it is at, which the caller sends back to ship only what it reviewed.</summary>
    protected virtual async Task AddDraftBranchAsync(SiteBuilderObjectChange change, GitPageFileLocation location, CancellationToken cancellationToken)
    {
        change.DraftBranch = location.Branch;
        change.DraftHead = await gitContentRepository.GetBranchHeadShaAsync(location.Repository, location.Branch, cancellationToken);
    }

    protected virtual async Task<string> GetBaseBranchAsync(CancellationToken cancellationToken) =>
        (await gitHubConnectionService.GetConnectionAsync(cancellationToken)).BaseBranch;

    protected virtual GitPageFileLocation GetLocation(string repository, string type, string path, ClaimsPrincipal user) =>
        gitContentOptions.Value.GetPageLocation(repository, type, path, user?.Identity?.Name);

    /// <summary>The draft branch of the request's draft owner, else the caller's own.</summary>
    protected virtual GitPageFileLocation GetDraftLocation(string repository, FilePageShipOperationRequest request, ClaimsPrincipal user) =>
        string.IsNullOrWhiteSpace(request.DraftOwner)
            ? GetLocation(repository, request.ContentType, request.Path, user)
            : gitContentOptions.Value.GetPageLocation(repository, request.ContentType, request.Path, request.DraftOwner.Trim());

    /// <summary>
    /// A commit message with the authenticated login recorded under it. Git authorship is signed with
    /// a shared address, so this trailer is the only attribution that survives — and because the
    /// server writes it from the identity on the request, it is one a client cannot dress up.
    /// </summary>
    protected virtual string GetCommitMessage(string summary, ClaimsPrincipal user) => GitCommitMessage.WithVcUser(summary, user?.Identity?.Name);

    protected virtual GitCommitAuthor GetCurrentAuthor(ClaimsPrincipal user) => gitContentOptions.Value.GetCommitAuthor(user);

    protected virtual string GetPageErrorsMessage(string path, IEnumerable<string> errors) =>
        $"\"{path}\" is not a page document that can be saved: {string.Join("; ", errors)}";

    /// <summary>A publish or an unpublish in blob storage, which reaches what the storefront serves at once.</summary>
    protected virtual SiteBuilderChangePreview CreateBlobPublishingPreview(string path, string from, string to, string action)
    {
        var preview = CreatePreview();
        preview.AddObject(FilePage, path).AddField(Status, from, to);
        preview.Warnings.Add(action);

        return preview;
    }

    protected virtual FilePageShipment CreateShipment(GitPublishState state, bool gitFlow)
    {
        var shipment = AbstractTypeFactory<FilePageShipment>.TryCreateInstance();
        shipment.State = state;
        shipment.GitFlow = gitFlow;

        return shipment;
    }

    protected virtual SiteBuilderChangePreview CreatePreview() => AbstractTypeFactory<SiteBuilderChangePreview>.TryCreateInstance();

    private static SiteBuilderOperationResult<FilePageShipment> Shipped(FilePageShipment shipment, FilePageOperationRequest request) =>
        SiteBuilderOperationResult.Success(shipment, affectsPublished: request.ReportImpact);

    /// <summary>A publish, an unpublish and a promotion reach what the storefront serves, even when there turns out to be nothing to merge.</summary>
    private static SiteBuilderOperationResult<FilePageShipment> DryRunShipment(SiteBuilderChangePreview preview)
    {
        preview.AffectsPublished = true;

        return SiteBuilderOperationResult.DryRun<FilePageShipment>(preview);
    }

    private static SiteBuilderContentRequest PageRequest(string storeId, string type, params string[] pages) =>
        new() { StoreId = storeId, ContentType = type ?? PagesContentType, Pages = pages };

    /// <summary>The page's own name, whichever of the two names the caller happens to hold.</summary>
    private static string WithoutDraftSuffix(string path) =>
        path.EndsWith(DraftSuffix, StringComparison.OrdinalIgnoreCase) ? path[..^DraftSuffix.Length] : path;

    private static JToken ParsePage(string json)
    {
        try
        {
            return JToken.Parse(json);
        }
        catch (JsonReaderException)
        {
            return JValue.CreateNull();
        }
    }
}
