using Microsoft.Extensions.DependencyInjection;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.Platform.Core.Jobs;
using VirtoCommerce.SiteBuilder.MCP.Jobs;
using VirtoCommerce.SiteBuilder.MCP.Services;
using VirtoCommerce.SiteBuilder.MCP.Tools;

namespace VirtoCommerce.SiteBuilder.MCP.Extensions;

public static class ServiceCollectionExtensions
{
    // Registered only when the MCP module is installed: nothing else queues these jobs.
    public static IServiceCollection AddSiteBuilderMcp(this IServiceCollection services)
    {
        services.AddMcpCapabilities<McpAssemblyMarker>();
        services.AddTransient<PageDraftJobRunner>();
        services.AddTransient<TranslationSources>();
        services.AddTransient<SiteBuilderJobUsers>();
        services.AddTransient<SiteThemeStates>();
        services.AddTransient<PublicationDryRuns>();
        services.AddTransient<FilePageShipping>();
        services.AddBackgroundJob<UpdateSectionsBulkJob, UpdateSectionsBulkJobPayload>(triggerable: false);
        services.AddBackgroundJob<SetTranslationsJob, SetTranslationsJobPayload>(triggerable: false);

        return services;
    }
}
