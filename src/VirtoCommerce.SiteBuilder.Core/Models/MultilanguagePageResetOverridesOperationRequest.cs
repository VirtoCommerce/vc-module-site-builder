namespace VirtoCommerce.SiteBuilder.Core.Models;

public class MultilanguagePageResetOverridesOperationRequest : MultilanguagePageOperationRequest
{
    /// <summary>Names of <see cref="MultilanguagePageInheritance.InheritableFields"/>.</summary>
    public IList<string> Fields { get; set; } = [];
}
