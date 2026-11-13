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

    /// <summary>A request running at the same time changed what this one relied on; what this one wrote is undone.</summary>
    ConcurrentChange,
}
