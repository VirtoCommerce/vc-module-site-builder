using System.Linq;
using System.Threading.Tasks;
using GraphQL;
using Microsoft.Extensions.Logging;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.XCMS.Core.Models;
using VirtoCommerce.XCMS.Core.Schemas;
using PageBuilderGroupedPageService = VirtoCommerce.PageBuilderModule.Core.Services.IGroupedPageService;

namespace VirtoCommerce.SiteBuilder.Xapi.Schemas;

/// <summary>
/// Loads the content of the x-cms builder page from Site Builder: x-cms reads it from Page Builder, which is not there
/// when Site Builder is installed instead.
/// </summary>
public class SiteBuilderPageItemType : BuilderPageItemType
{
    private readonly IGroupedPageService _groupedPageService;

    public SiteBuilderPageItemType(
        IOptionalDependency<PageBuilderGroupedPageService> pageBuilderGroupedPageService,
        IGroupedPageService groupedPageService)
        : base(pageBuilderGroupedPageService)
    {
        _groupedPageService = groupedPageService;

        // the name GraphQL would take from this class would rename the type in the schema
        Name = "BuilderPageItemType";
    }

    protected override async Task<string> LoadContent(IResolveFieldContext<BuilderPageItem> context)
    {
        var pageId = context.Source.PageId;

        if (!context.Source.Content.IsNullOrEmpty() || pageId == null)
        {
            return context.Source.Content;
        }

        var contentProvider = GetPageBuilderContentProvider(context);

        if (contentProvider == null)
        {
            return await _groupedPageService.LoadContent(pageId, context.CancellationToken);
        }

        // The provider serves the page as it is published and indexed, with shared components resolved, which the
        // preview needs: the raw content keeps references the storefront cannot render.
        var pages = await contentProvider.GetByIdsAsync([pageId]);
        var content = pages?.FirstOrDefault(x => x.Id.EqualsIgnoreCase(pageId))?.Content;

        if (content == null)
        {
            GetLogger(context)?.LogWarning(
                "Site Builder content provider returned no resolved content for page '{PageId}'. The preview will be empty.",
                pageId);
        }

        return content;
    }
}
