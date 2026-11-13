using Microsoft.Extensions.DependencyInjection;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.Platform.Core.Jobs;
using VirtoCommerce.SiteBuilder.MCP.Jobs;

namespace VirtoCommerce.SiteBuilder.MCP;

public static class SiteBuilderMcpRegistration
{
    // The toolset and what its bulk tools run: registered only when the MCP module is installed, as nothing else
    // queues these jobs.
    public static IServiceCollection AddSiteBuilderMcp(this IServiceCollection services)
    {
        services.AddMcpCapabilities<McpAssemblyMarker>();
        services.AddTransient<PageDraftJobRunner>();
        services.AddTransient<TranslationSources>();
        services.AddTransient<SiteBuilderJobUsers>();
        services.AddBackgroundJob<UpdateSectionsBulkJob, UpdateSectionsBulkJobPayload>(triggerable: false);
        services.AddBackgroundJob<SetTranslationsJob, SetTranslationsJobPayload>(triggerable: false);

        return services;
    }
}
