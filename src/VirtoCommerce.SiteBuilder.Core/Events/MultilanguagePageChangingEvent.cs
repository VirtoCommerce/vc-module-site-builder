using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.Events;

namespace VirtoCommerce.SiteBuilder.Core.Events;

public class MultilanguagePageChangingEvent(IEnumerable<GenericChangedEntry<MultilanguagePage>> changedEntries)
    : GenericChangedEntryEvent<MultilanguagePage>(changedEntries);
