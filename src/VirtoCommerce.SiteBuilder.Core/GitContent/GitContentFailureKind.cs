namespace VirtoCommerce.SiteBuilder.Core.GitContent;

// What went wrong, for an answer that may not show the message: the message can carry GitHub's own text, repository
// names and configuration keys, which only an administrator should read.
public enum GitContentFailureKind
{
    Unknown,
    NotConfigured,
    Unreachable,
    NoAccess,
}
