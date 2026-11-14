using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.Platform.Core.Jobs;

namespace VirtoCommerce.SiteBuilder.MCP;

public static class BulkJobs
{
    // Without a background job engine the platform cannot run the job: the agent is told so in a sentence, and that
    // nothing was changed.
    public static async Task<McpJobHandle> EnqueueAsync<THandler>(this IMcpJobDispatcher jobDispatcher, RequestContext<CallToolRequestParams> context,
        object payload, McpJobRequest request, string alternative)
        where THandler : class
    {
        try
        {
            return await jobDispatcher.EnqueueAsync<THandler>(context, payload, request);
        }
        catch (Exception ex) when (ex is BackgroundJobEngineNotInstalledException or McpException)
        {
            throw new McpException("Background jobs are not available on this platform (the Background Jobs module is not installed), " +
                $"so the change was not started and nothing was changed. Tell the user to ask the administrator to install it, or {alternative}");
        }
    }
}
