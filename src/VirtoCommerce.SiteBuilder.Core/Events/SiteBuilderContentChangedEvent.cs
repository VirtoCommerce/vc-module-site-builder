using Newtonsoft.Json;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.Events;

namespace VirtoCommerce.SiteBuilder.Core.Events;

[method: JsonConstructor]
public class SiteBuilderContentChangedEvent(
    string contentType,
    IEnumerable<GenericChangedEntry<FileEntity>> changedEntries)
    : GenericChangedEntryEvent<FileEntity>(changedEntries)
{
    public string ContentType { get; } = contentType;
}
