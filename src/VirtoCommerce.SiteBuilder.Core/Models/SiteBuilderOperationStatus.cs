namespace VirtoCommerce.SiteBuilder.Core.Models;

public enum SiteBuilderOperationStatus
{
    Succeeded,
    Forbidden,
    NotFound,
    Invalid,

    /// <summary>Something was saved by someone else since the caller read it: nothing is written.</summary>
    Conflict,

    /// <summary>The caller has to send the version it read before it may write.</summary>
    VersionRequired,
}
