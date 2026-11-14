using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.Platform.Core.Security;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Jobs;

// A page of a bulk job, named in its report.
public interface ISiteBuilderJobPage
{
    string PageId { get; }
}

// The user a job of a bulk tool runs as: the one who called the tool, as a sign-in would give them now, with the
// roles and permissions they have when each page is changed rather than when the job was queued.
public class SiteBuilderJobUsers(
    Func<UserManager<ApplicationUser>> userManagerFactory,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsPrincipalFactory,
    IUserNameResolver userNameResolver)
{
    public const string AccountGone = "the account that started the job no longer exists or is locked out";

    // Null when the account no longer exists or is locked out.
    public virtual async Task<ClaimsPrincipal> CreateAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        using var userManager = userManagerFactory();
        var account = await userManager.FindByIdAsync(userId);
        if (account == null || (userManager.SupportsUserLockout && await userManager.IsLockedOutAsync(account)))
        {
            return null;
        }

        return await claimsPrincipalFactory.CreateAsync(account);
    }

    // Why the user may not change any page now, or null. The tools require builder:update granted in every store, as
    // the designer's save does; the store of each page is checked page by page.
    public static string GetRefusal(ClaimsPrincipal user)
    {
        if (user == null)
        {
            return AccountGone;
        }

        return user.GetMcpPermissionGrant(Update)?.IsGlobal == true ? null : "the account no longer has builder:update";
    }

    // Runs the pages of a job in order. Before each page the account is read again and its principal built afresh, so a
    // role or permission taken away since the call refuses the next page, and an account deleted or locked out stops the
    // job there: that page is refused and the rest are not reached. Returns why the job stopped early, or null.
    public virtual async Task<string> RunPagesAsync<TPage>(string userId, IList<TPage> pages, PageJobTally tally,
        Func<ClaimsPrincipal, TPage, Task<PageJobResult>> runPage, Func<int, Task> reportProgress, CancellationToken cancellationToken)
        where TPage : ISiteBuilderJobPage
    {
        for (var i = 0; i < pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = pages[i];
            var user = await CreateAsync(userId);
            var refusal = GetRefusal(user);
            if (refusal == null)
            {
                SetCurrentUser(user);
            }

            tally.Add(page.PageId, refusal == null ? await runPage(user, page) : new PageJobResult(PageJobOutcome.Refused, refusal));
            await reportProgress(i + 1);

            if (user == null)
            {
                return refusal;
            }
        }

        return null;
    }

    // The last message of a job that stopped early: why, how far it got, and what it did until then.
    public static string DescribeStop(string stopReason, int total, PageJobTally tally, string summary) =>
        $"Stopped after {tally.Total} of {total} pages: {stopReason}; {total - tally.Total} pages were not reached. {summary}";

    // Entities saved by the job name the user as their author; without it a job has no user name to record.
    public virtual void SetCurrentUser(ClaimsPrincipal user)
    {
        userNameResolver.SetCurrentUserName(user.Identity?.Name);
    }
}
