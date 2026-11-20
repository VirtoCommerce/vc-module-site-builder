namespace VirtoCommerce.SiteBuilder.Core.Models;

public class MultilanguagePageResetOverridesOperationRequest : MultilanguagePageOperationRequest
{
    /// <summary>Names of <see cref="MultilanguagePageInheritance.InheritableFields"/>.</summary>
    public IList<string> Fields { get; set; } = [];

    /// <summary>The reset is refused when it would change what a published language version serves, as read under the lock of each save.</summary>
    public bool RequireUnpublished { get; set; }

    /// <summary>The version of the multilanguage page whose values the versions take; <c>null</c> skips the check.</summary>
    public string ExpectedMultilanguagePageVersion { get; set; }

    /// <summary>
    /// The settings version of each language version the reset changes, by id; refused when one changed, appeared or went, each compared again
    /// under the lock of its save. <c>null</c> skips the check.
    /// </summary>
    public IDictionary<string, string> ExpectedSettingsVersions { get; set; }
}
