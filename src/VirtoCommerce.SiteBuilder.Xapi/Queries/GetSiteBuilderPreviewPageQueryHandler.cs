using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VirtoCommerce.ContentModule.Core.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.StoreModule.Core.Services;
using VirtoCommerce.Xapi.Core.Infrastructure;
using VirtoCommerce.XCMS.Core.Models;
using VirtoCommerce.XCMS.Core.Queries;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Xapi.Queries;

/// <summary>
/// Answers the x-cms <c>builderPage</c> query from Site Builder pages: x-cms reads Page Builder services, which are not
/// there when Site Builder is installed instead.
/// </summary>
public class GetSiteBuilderPreviewPageQueryHandler(
    ISiteBuilderPageSearchService pageSearchService,
    IGroupedPageSearchService groupedPageSearchService,
    IContentService contentService,
    IStoreService storeService
) : IQueryHandler<GetBuilderPageQuery, BuilderPageItem>
{
    public virtual async Task<BuilderPageItem> Handle(GetBuilderPageQuery request, CancellationToken cancellationToken)
    {
        var store = await storeService.GetByIdAsync(request.StoreId);

        var pagesEnabled = store.Settings.GetValue<bool>(Pages.Core.ModuleConstants.Settings.General.Enable);

        if (!pagesEnabled)
        {
            return await GetStaticContentPage(request);
        }

        var (page, group) = await GetPage(request);

        if (page != null && group == null)
        {
            group = await GetGroupById(request.StoreId, page.GroupId);
        }

        var pageItem = AbstractTypeFactory<BuilderPageItem>.TryCreateInstance();

        pageItem.PageId = page?.Id;
        pageItem.Permalink = group?.Permalink;
        return pageItem;
    }

    protected virtual async Task<BuilderPageItem> GetStaticContentPage(GetBuilderPageQuery request)
    {
        var file = await contentService.GetFileContentAsync(request.PageId);

        var result = AbstractTypeFactory<BuilderPageItem>.TryCreateInstance();
        result.PageId = request.PageId;
        result.Permalink = file?.Permalink;
        result.Content = file?.Content;

        return result;
    }

    protected virtual async Task<(SiteBuilderPage, GroupedSiteBuilderPage)> GetPage(GetBuilderPageQuery request)
    {
        var page = await GetPageById(request.StoreId, request.PageId);
        GroupedSiteBuilderPage group = null;
        if (page == null)
        {
            group = await GetGroupById(request.StoreId, request.PageId);
            if (group != null)
            {
                var pageModel = group.Pages.FirstOrDefault(x => x.Status == Draft)
                    ?? group.Pages.FirstOrDefault(x => x.Status == Published)
                    ?? group.Pages.OrderByDescending(x => x.ModifiedDate).FirstOrDefault(x => x.Status == Archived);
                if (pageModel != null)
                {
                    request.PageId = pageModel.Id;
                    page = await GetPageById(request.StoreId, pageModel.Id);
                }
            }
        }

        return (page, group);
    }

    protected virtual async Task<SiteBuilderPage> GetPageById(string storeId, string pageId)
    {
        var criteria = AbstractTypeFactory<SiteBuilderPageSearchCriteria>.TryCreateInstance();
        criteria.StoreId = storeId;
        criteria.ObjectIds = [pageId];

        var searchResult = await pageSearchService.SearchAsync(criteria);

        return searchResult.Results.FirstOrDefault();
    }

    protected virtual async Task<GroupedSiteBuilderPage> GetGroupById(string storeId, string groupId)
    {
        var criteria = AbstractTypeFactory<SiteBuilderPageSearchCriteria>.TryCreateInstance();
        criteria.StoreId = storeId;
        criteria.ObjectIds = [groupId];

        var searchResult = await groupedPageSearchService.SearchAsync(criteria);

        return searchResult.Results.FirstOrDefault();
    }
}
