using System.Security.Claims;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Core.GitContent;

public static class GitContentOptionsExtensions
{
    public static GitCommitAuthor GetCommitAuthor(this GitContentOptions options, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(options);

        var userName = user?.Identity?.Name;

        var author = AbstractTypeFactory<GitCommitAuthor>.TryCreateInstance();
        author.Name = string.IsNullOrEmpty(userName) ? options.FallbackAuthorName : userName;
        author.Email = user?.FindFirstValue(ClaimTypes.Email) ?? options.FallbackAuthorEmail;
        return author;
    }

    /// <summary>Path and branch come from the same content path, so a page and a blog article that share a relative url stay two files on two branches.</summary>
    public static GitPageFileLocation GetPageLocation(this GitContentOptions options, string repository, string contentType, string path, string userName)
    {
        ArgumentNullException.ThrowIfNull(options);

        var contentPath = GitPageLocation.ContentPath(contentType, path);

        return new GitPageFileLocation(
            repository,
            GitPageLocation.RepoPath(options.PagesRoot, contentPath),
            GitPageLocation.BranchFor(options.BranchTemplate, userName, contentPath));
    }

    /// <summary>The branch a publish or an unpublish pinned to an approved commit of the editor's work branch ships from.</summary>
    public static string GetPublishBranch(this GitContentOptions options, string contentType, string path, string userName)
    {
        ArgumentNullException.ThrowIfNull(options);

        return GitPageLocation.BranchFor(options.PublishBranchTemplate, userName, GitPageLocation.ContentPath(contentType, path));
    }

    /// <summary>
    /// Promotion is per page, not per editor: the branch carries no {user}, so two people promoting the same page reuse one branch and
    /// one pull request instead of racing with two.
    /// </summary>
    public static string GetPromoteBranch(this GitContentOptions options, string contentType, string path)
    {
        ArgumentNullException.ThrowIfNull(options);

        return GitPageLocation.BranchFor(options.PromoteBranchTemplate, userName: null, GitPageLocation.ContentPath(contentType, path));
    }
}
