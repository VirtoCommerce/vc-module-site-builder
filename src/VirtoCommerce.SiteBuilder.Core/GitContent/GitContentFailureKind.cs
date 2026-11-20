namespace VirtoCommerce.SiteBuilder.Core.GitContent;

// Lets an answer say what failed without the message, which can carry repository names and configuration keys.
public enum GitContentFailureKind
{
    Unknown,
    NotConfigured,
    Unreachable,
    NoAccess,
}
