using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using VirtoCommerce.ContentModule.Core.Model;
using VirtoCommerce.ContentModule.Core.Services;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Extensions;
using VirtoCommerce.Platform.Core.Common;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Data.Services;

[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The services come from dependency injection; a parameter object would only hide them and change the constructor that subclasses call.")]
public class SiteBuilderPublishStatusQueryService(
    IAuthorizationService authorizationService,
    IValidator<SiteBuilderContentRequest> requestValidator,
    IGroupedPageService groupedPageService,
    IPublishingService publishingService,
    IGitContentPolicy gitContentPolicy,
    IGitContentRepository gitContentRepository,
    IGitContentPublisher gitContentPublisher,
    IGitLivePageService gitLivePageService,
    IGitHubConnectionService gitHubConnectionService,
    IOptions<GitContentOptions> gitContentOptions) : ISiteBuilderPublishStatusQueryService
{
    private const string PagesContentType = "pages";
    // How a client names the two flows in the publish-status answer.
    private const string GitFlow = "git";
    private const string BlobFlow = "blob";

    public virtual Task<SiteBuilderOperationResult<PagePublishStatus>> GetFilePageStatusAsync(SiteBuilderFilePageQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetFilePageStatusInternalAsync(request, user, cancellationToken);
    }

    private async Task<SiteBuilderOperationResult<PagePublishStatus>> GetFilePageStatusInternalAsync(SiteBuilderFilePageQueryRequest request,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var contentRequest = new SiteBuilderContentRequest
        {
            StoreId = request.StoreId,
            ContentType = request.ContentType ?? PagesContentType,
            Pages = string.IsNullOrEmpty(request.Path) ? [] : [request.Path],
        };
        if (await CheckRequestAsync(contentRequest, request.BindingErrors, user, cancellationToken) is { } refused)
        {
            return refused.As<PagePublishStatus>();
        }

        var storeId = request.StoreId;
        var type = request.ContentType;
        var path = request.Path;
        var storeRepository = await gitContentPolicy.GetRepositoryInfoAsync(storeId, cancellationToken);
        var gitFlow = storeRepository != null;

        // A page that does not exist yet — the blade asking about a page it is about to create — has
        // no status; what the caller is after in that case is the flow.
        if (string.IsNullOrEmpty(path))
        {
            return SiteBuilderOperationResult.Success(CreateStatus(published: false, hasChanges: false, pending: false, gitFlow));
        }

        if (!gitFlow)
        {
            var status = await publishingService.PublishStatusAsync(type ?? PagesContentType, storeId, path);
            return SiteBuilderOperationResult.Success(CreateStatus(status.Published, status.HasChanges, pending: false, gitFlow: false));
        }

        return SiteBuilderOperationResult.Success(await GetGitStatusAsync(storeRepository, storeId, type, path, user, cancellationToken));
    }

    private async Task<PagePublishStatus> GetGitStatusAsync(StoreRepository storeRepository, string storeId, string type, string path, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var baseBranch = (await gitHubConnectionService.GetConnectionAsync(cancellationToken)).BaseBranch;
        var location = gitContentOptions.Value.GetPageLocation(storeRepository.Name, type, path, user?.Identity?.Name);

        var published = await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, baseBranch, cancellationToken);
        var draft = await gitContentRepository.ReadFileAsync(location.Repository, location.RepoPath, location.Branch, cancellationToken);
        var pending = await gitContentPublisher.GetOpenPullRequestAsync(location.Repository, location.Branch, cancellationToken);

        return CreateStatus(
            published: published != null,
            // a draft that says the same thing as production is not a change, whatever its history
            hasChanges: draft != null && !PageJson.AreSame(draft, published),
            pending: pending != null,
            gitFlow: true,
            // the pull request is open but nothing will merge it: publishing again is what finishes it
            awaitingMerge: pending is { AutoMerging: false },
            production: await GetProductionStatusAsync(location.Repository, location.RepoPath, published, type, path, cancellationToken),
            liveBehind: storeRepository.IsPlatformManaged &&
                        await gitLivePageService.IsLivePageBehindAsync(storeId, type ?? PagesContentType, path, location, published, cancellationToken));
    }

    public virtual Task<SiteBuilderOperationResult<FilePublishStatus>> GetGroupedPageStatusAsync(GroupedPageQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return GetGroupedPageStatusInternalAsync(request, user);
    }

    private async Task<SiteBuilderOperationResult<FilePublishStatus>> GetGroupedPageStatusInternalAsync(GroupedPageQueryRequest request, ClaimsPrincipal user)
    {
        var groupedPage = await groupedPageService.GetNoCloneAsync(request.GroupId);
        if (groupedPage == null)
        {
            return SiteBuilderOperationResult.NotFound().As<FilePublishStatus>();
        }

        if (await authorizationService.CheckStoreOperationAsync(user, groupedPage) is { } forbidden)
        {
            return forbidden.As<FilePublishStatus>();
        }

        var result = new FilePublishStatus
        {
            Published = groupedPage.Status == Published,
            HasChanges = groupedPage.HasChanges,
        };

        return SiteBuilderOperationResult.Success(result);
    }

    /// <summary>Store access first, so a user bound to another store cannot tell from a refused request which stores exist.</summary>
    protected virtual async Task<SiteBuilderOperationResult> CheckRequestAsync(SiteBuilderContentRequest request, IList<string> bindingErrors,
        ClaimsPrincipal user, CancellationToken cancellationToken) =>
        await authorizationService.CheckStoreOperationAsync(user, request)
        ?? await requestValidator.CheckOperationRequestAsync(request, bindingErrors, cancellationToken);

    /// <summary>
    /// Where this page stands on the production branch, or <c>null</c> when the installation has none.
    /// <para>
    /// Reported separately from the fields above rather than folded into them: a page can be published
    /// and production still be serving last week's version of it, and that in-between state is the one
    /// that makes an editor say the site did not update. Nothing before this could express it.
    /// </para>
    /// </summary>
    protected virtual async Task<PageProductionStatus> GetProductionStatusAsync(string repository, string repoPath, string onBase, string type, string path,
        CancellationToken cancellationToken)
    {
        var options = gitContentOptions.Value;
        if (string.IsNullOrWhiteSpace(options.ReleaseBranch))
        {
            return null;
        }

        var onRelease = await gitContentRepository.ReadFileAsync(repository, repoPath, options.ReleaseBranch, cancellationToken);
        var pending = await gitContentPublisher.GetOpenPullRequestAsync(repository, options.GetPromoteBranch(type, path), cancellationToken);

        var production = AbstractTypeFactory<PageProductionStatus>.TryCreateInstance();
        production.Published = onRelease != null;
        // only a page on the base branch can be behind: a page never published has simply not started
        production.Behind = onBase != null && !PageJson.AreSame(onBase, onRelease);
        production.Pending = pending != null;
        production.AwaitingMerge = pending is { AutoMerging: false };
        return production;
    }

    protected virtual PagePublishStatus CreateStatus(bool published, bool hasChanges, bool pending, bool gitFlow,
        bool awaitingMerge = false, PageProductionStatus production = null, bool liveBehind = false)
    {
        var status = AbstractTypeFactory<PagePublishStatus>.TryCreateInstance();
        status.Published = published;
        status.HasChanges = hasChanges;
        status.Pending = pending;
        // Told apart from pending on purpose: both mean "a pull request is open", but only this one
        // means the merge is not going to happen unless somebody asks for it again.
        status.AwaitingMerge = awaitingMerge;
        status.Flow = gitFlow ? GitFlow : BlobFlow;
        status.Production = production;
        status.LiveBehind = liveBehind;
        return status;
    }
}
