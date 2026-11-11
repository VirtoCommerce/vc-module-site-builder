using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.Events;

namespace VirtoCommerce.SiteBuilder.Core.Events;

public class SiteBuilderSharedComponentChangedEvent(
    IEnumerable<GenericChangedEntry<SiteBuilderSharedComponent>> changedEntries)
    : GenericChangedEntryEvent<SiteBuilderSharedComponent>(changedEntries);
