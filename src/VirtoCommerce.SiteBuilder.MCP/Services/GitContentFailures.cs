using Microsoft.Extensions.Logging;
using VirtoCommerce.SiteBuilder.Core.GitContent;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

// The exception's message can carry GitHub's text, repository names and configuration keys that the reader of the
// answer may not see, so it goes to the log and the answer names only the kind of failure.
public static class GitContentFailures
{
    public const string NotConfigured = "The store's content repository is not set up correctly on this platform; an administrator can check the " +
        "Git content settings of Site Builder.";

    public const string Unreachable = "GitHub, which keeps the store's content, could not be reached; try again later.";

    public const string NoAccess = "The platform has no access to the store's content repository; an administrator can check the GitHub App's " +
        "access to it.";

    public const string Unknown = "The store's content repository could not complete the request; try again later, or ask an administrator to " +
        "check the platform log.";

    public static string Describe(ILogger logger, GitContentException exception)
    {
        logger.LogWarning(exception, "The store's content repository failed ({Kind})", exception.Kind);

        return exception.Kind switch
        {
            GitContentFailureKind.NotConfigured => NotConfigured,
            GitContentFailureKind.Unreachable => Unreachable,
            GitContentFailureKind.NoAccess => NoAccess,
            _ => Unknown,
        };
    }
}
