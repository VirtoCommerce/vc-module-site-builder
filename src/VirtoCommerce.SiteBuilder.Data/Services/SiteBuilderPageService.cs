using VirtoCommerce.SiteBuilder.Core.Events;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.Platform.Caching;
using VirtoCommerce.Platform.Core.Caching;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Events;
using VirtoCommerce.Platform.Data.GenericCrud;

namespace VirtoCommerce.SiteBuilder.Data.Services;

public class SiteBuilderPageService(
    Func<ISiteBuilderRepository> repositoryFactory,
    IPlatformMemoryCache platformMemoryCache,
    IEventPublisher eventPublisher)
    : CrudService<SiteBuilderPage, SiteBuilderPageEntity, SiteBuilderPageChangingEvent, SiteBuilderPageChangedEvent>(
        repositoryFactory, platformMemoryCache, eventPublisher), ISiteBuilderPageService
{
    protected override Task<IList<SiteBuilderPageEntity>> LoadEntities(IRepository repository, IList<string> ids, string responseGroup)
    {
        return ((ISiteBuilderRepository)repository).GetSiteBuilderPagesByIdsAsync(ids, responseGroup);
    }

    // A page is part of its group's cached aggregate: GroupedSiteBuilderPage carries the whole Pages collection.
    // The base implementation only expires this entity's own region, so saving or deleting a page would leave the
    // group cached with a stale page list — and nothing would ever correct it, because the changed-event handlers
    // re-read the group through the very cache that was not invalidated. PublishGroup deletes the superseded pages
    // right after saving the group, so without this cascade the group keeps serving deleted page ids until the
    // process restarts.
    protected override void ClearCache(IList<SiteBuilderPage> models)
    {
        base.ClearCache(models);

        foreach (var groupId in models.Select(x => x.GroupId).Where(x => !string.IsNullOrEmpty(x)).Distinct())
        {
            GenericCachingRegion<GroupedSiteBuilderPage>.ExpireTokenForKey(groupId);
        }
    }
}
