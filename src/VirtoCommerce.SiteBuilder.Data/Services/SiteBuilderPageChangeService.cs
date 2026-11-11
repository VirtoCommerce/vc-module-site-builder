using Microsoft.EntityFrameworkCore;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Repositories;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public sealed class SiteBuilderPageChangeService(
    Func<ISiteBuilderRepository> repositoryFactory)
    : ISiteBuilderPageChangeService
{
    private const int QueryBatchSize = 500;

    public async Task<IReadOnlyDictionary<string, DateTime>> GetEffectiveChangeDatesAsync(
        IEnumerable<SiteBuilderPage> pages,
        CancellationToken cancellationToken = default)
    {
        using var repository = repositoryFactory();
        var result = pages
            .Where(page => !string.IsNullOrWhiteSpace(page.Id))
            .ToDictionary(
                page => page.Id,
                page => page.ModifiedDate ?? page.CreatedDate,
                StringComparer.OrdinalIgnoreCase);

        foreach (var batch in result.Keys.Chunk(QueryBatchSize))
        {
            var componentDates = await repository.SiteBuilderSharedComponentReferences
                .Where(reference => batch.Contains(reference.PageId))
                .Join(
                    repository.SiteBuilderSharedComponents,
                    reference => reference.SharedComponentId,
                    component => component.Id,
                    (reference, component) => new
                    {
                        reference.PageId,
                        ChangeDate = component.ModifiedDate ?? component.CreatedDate,
                    })
                .GroupBy(x => x.PageId)
                .Select(group => new
                {
                    PageId = group.Key,
                    ChangeDate = group.Max(x => x.ChangeDate),
                })
                .ToListAsync(cancellationToken);

            foreach (var componentDate in componentDates)
            {
                if (result.TryGetValue(componentDate.PageId, out var pageDate) &&
                    componentDate.ChangeDate > pageDate)
                {
                    result[componentDate.PageId] = componentDate.ChangeDate;
                }
            }
        }

        return result;
    }
}
