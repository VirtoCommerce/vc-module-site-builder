using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface ISiteBuilderPageChangeService
{
    Task<IReadOnlyDictionary<string, DateTime>> GetEffectiveChangeDatesAsync(
        IEnumerable<SiteBuilderPage> pages,
        CancellationToken cancellationToken = default);
}
