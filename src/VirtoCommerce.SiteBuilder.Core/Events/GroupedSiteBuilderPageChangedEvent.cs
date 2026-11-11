using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.Events;

namespace VirtoCommerce.SiteBuilder.Core.Events;

public class GroupedSiteBuilderPageChangedEvent(IEnumerable<GenericChangedEntry<GroupedSiteBuilderPage>> changedEntries)
    : GenericChangedEntryEvent<GroupedSiteBuilderPage>(changedEntries);
