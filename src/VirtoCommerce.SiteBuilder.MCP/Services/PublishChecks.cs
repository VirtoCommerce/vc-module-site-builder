using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Models;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

public static class PublishChecks
{
    public static void EnsureAllowed(SiteBuilderOperationResult result, string forbidden)
    {
        if (result.Status == SiteBuilderOperationStatus.Forbidden)
        {
            throw new McpForbiddenException(forbidden);
        }
    }

    public static McpFinding Blocker(string id, string title, string evidence, string fix, string category, GroupedSiteBuilderPage group = null) =>
        Finding(McpFindingSeverities.Critical, id, title, evidence, fix, category, group);

    public static McpFinding Warning(string id, string title, string evidence, string fix, string category, GroupedSiteBuilderPage group = null) =>
        Finding(McpFindingSeverities.Warning, id, title, evidence, fix, category, group);

    public static McpFinding Info(string id, string title, string evidence, string fix, string category, GroupedSiteBuilderPage group = null) =>
        Finding(McpFindingSeverities.Info, id, title, evidence, fix, category, group);

    private static McpFinding Finding(string severity, string id, string title, string evidence, string fix, string category, GroupedSiteBuilderPage group) => new()
    {
        Id = id,
        Severity = severity,
        Title = title,
        Evidence = evidence,
        Fix = fix,
        Category = category,
        ObjectType = group == null ? null : ChangePreview.Page,
        ObjectId = group?.Id,
        ObjectName = group?.Name,
    };
}
