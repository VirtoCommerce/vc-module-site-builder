using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.Events;

namespace VirtoCommerce.SiteBuilder.Core.Events;

public class GroupedSiteBuilderPageChangingEvent : GenericChangedEntryEvent<GroupedSiteBuilderPage>
{
    public GroupedSiteBuilderPageChangingEvent(IEnumerable<GenericChangedEntry<GroupedSiteBuilderPage>> changedEntries)
        : base(changedEntries)
    {
    }
}
