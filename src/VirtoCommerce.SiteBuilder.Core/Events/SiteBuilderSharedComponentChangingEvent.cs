using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.Events;

namespace VirtoCommerce.SiteBuilder.Core.Events;

public class SiteBuilderSharedComponentChangingEvent(
    IEnumerable<GenericChangedEntry<SiteBuilderSharedComponent>> changedEntries)
    : GenericChangedEntryEvent<SiteBuilderSharedComponent>(changedEntries);
