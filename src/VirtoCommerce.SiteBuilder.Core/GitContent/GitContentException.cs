using System;

namespace VirtoCommerce.SiteBuilder.Core.GitContent;

public class GitContentException : Exception
{
    public GitContentException(string message)
        : base(message)
    {
    }

    public GitContentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GitContentException(string message, GitContentFailureKind kind, Exception innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public GitContentFailureKind Kind { get; }

    public static GitContentException RepositoryUnreachable(string repository) =>
        new($"The GitHub repository \"{repository}\" does not exist, or the platform has no access to it.", GitContentFailureKind.NoAccess);
}
