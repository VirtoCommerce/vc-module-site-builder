namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>What has to hold for a save of a page group to go ahead, read under the lock of the save.</summary>
public class GroupedPageSaveCondition
{
    /// <summary>
    /// The save is refused when it would change what a published page serves: the page is published and one of its settings changes, the rule
    /// <see cref="SiteBuilderChangePreview.AffectsPublished"/> tells.
    /// </summary>
    public bool RequireUnpublished { get; set; }

    /// <summary>The version of the group's settings, as <see cref="SiteBuilderContentVersion.CreateForGroupSettings"/> makes it; <c>null</c> saves over whatever they hold.</summary>
    public string ExpectedSettingsVersion { get; set; }

    /// <summary>A language version whose source revision has to be <see cref="ExpectedSourceRevisionDate"/>; its lock is taken with the group's.</summary>
    public string SourceGroupId { get; set; }

    public DateTime? ExpectedSourceRevisionDate { get; set; }
}
