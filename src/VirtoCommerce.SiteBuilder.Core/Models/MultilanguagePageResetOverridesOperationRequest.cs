namespace VirtoCommerce.SiteBuilder.Core.Models;

public class MultilanguagePageResetOverridesOperationRequest : MultilanguagePageOperationRequest
{
    /// <summary>Names of <see cref="MultilanguagePageInheritance.InheritableFields"/>.</summary>
    public IList<string> Fields { get; set; } = [];

    /// <summary>The reset is refused when it would change what a published language version serves, as read under the lock of each save.</summary>
    public bool RequireUnpublished { get; set; }

    /// <summary>
    /// The version of the multilanguage page the caller reviewed, whose values the versions take, as a dry run tells it in
    /// <see cref="SiteBuilderObjectChange.ETag"/>: the reset is refused once someone changed it. <c>null</c> resets to whatever it holds.
    /// </summary>
    public string ExpectedMultilanguagePageVersion { get; set; }

    /// <summary>
    /// The version of the settings of every language version the reset changes, by its id, as a dry run tells them in
    /// <see cref="SiteBuilderObjectChange.ObjectId"/> and <see cref="SiteBuilderObjectChange.ETag"/>. The reset is refused when the versions it
    /// changes are not those any more: one changed, appeared or went; each is compared again under the lock of its save. <c>null</c> resets
    /// whatever the versions hold.
    /// </summary>
    public IDictionary<string, string> ExpectedSettingsVersions { get; set; }
}
