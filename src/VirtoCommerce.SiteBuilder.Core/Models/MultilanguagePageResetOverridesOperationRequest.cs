namespace VirtoCommerce.SiteBuilder.Core.Models;

public class MultilanguagePageResetOverridesOperationRequest : MultilanguagePageOperationRequest
{
    /// <summary>Names of <see cref="MultilanguagePageInheritance.InheritableFields"/>.</summary>
    public IList<string> Fields { get; set; } = [];

    /// <summary>The reset is refused when it would change what a published language version serves, as read under the lock of each save.</summary>
    public bool RequireUnpublished { get; set; }
}
