using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.Events;

namespace VirtoCommerce.SiteBuilder.Core.Events;

public class SiteBuilderPageChangingEvent : GenericChangedEntryEvent<SiteBuilderPage>
{
    public SiteBuilderPageChangingEvent(IEnumerable<GenericChangedEntry<SiteBuilderPage>> changedEntries)
        : base(changedEntries)
    {
    }
}
