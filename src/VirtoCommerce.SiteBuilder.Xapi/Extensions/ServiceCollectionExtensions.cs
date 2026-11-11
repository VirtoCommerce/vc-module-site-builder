using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VirtoCommerce.SiteBuilder.Xapi.Queries;
using VirtoCommerce.SiteBuilder.Xapi.Schemas;
using VirtoCommerce.Xapi.Core.Extensions;
using VirtoCommerce.XCMS.Core.Models;
using VirtoCommerce.XCMS.Core.Queries;
using VirtoCommerce.XCMS.Core.Schemas;

namespace VirtoCommerce.SiteBuilder.Xapi.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Serves the x-cms <c>builderPage</c> query from Site Builder. Call it after x-cms registered its own handler:
    /// MediatR keeps the first handler registered for a request.
    /// </summary>
    public static IServiceCollection AddSiteBuilderPreview(this IServiceCollection services)
    {
        services.RemoveAll<IRequestHandler<GetBuilderPageQuery, BuilderPageItem>>();
        services.AddTransient<IRequestHandler<GetBuilderPageQuery, BuilderPageItem>, GetSiteBuilderPreviewPageQueryHandler>();
        services.OverrideGraphType<BuilderPageItemType, SiteBuilderPageItemType>();

        return services;
    }
}
