using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.Events;

namespace VirtoCommerce.SiteBuilder.Core.Events;

public class SiteBuilderPageChangedEvent : GenericChangedEntryEvent<SiteBuilderPage>
{
    public SiteBuilderPageChangedEvent(IEnumerable<GenericChangedEntry<SiteBuilderPage>> changedEntries)
        : base(changedEntries)
    {
    }
}
