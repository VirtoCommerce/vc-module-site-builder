using VirtoCommerce.Platform.Core.Events;

namespace VirtoCommerce.SiteBuilder.Core.Events;

public class SiteBuilderSharedComponentContentChangedEvent(IEnumerable<string> sharedComponentIds) : IEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public int Version { get; set; }

    public DateTimeOffset TimeStamp { get; set; } = DateTimeOffset.UtcNow;

    public IList<string> SharedComponentIds { get; } = sharedComponentIds
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}
