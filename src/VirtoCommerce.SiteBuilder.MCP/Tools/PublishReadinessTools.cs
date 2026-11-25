using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.MCP.Extensions;
using VirtoCommerce.SiteBuilder.MCP.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

[SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters",
    Justification = "The services come from dependency injection; a parameter object would only hide them and change the constructor that subclasses call.")]
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class PublishReadinessTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService pageContentService,
    IMultilanguagePageService multilanguagePageService,
    PublicationDryRuns dryRuns,
    ISiteBuilderPageListService pageListService,
    IContentHistory contentHistory,
    ILogger<PublishReadinessTools> logger)
{
    private const string PublishAction = "publish";
    private const string UnpublishAction = "unpublish";

    private const int HistoryVersions = 20;

    [McpServerTool(Name = "get_publish_readiness", ReadOnly = true, Destructive = false, Idempotent = true)]
    [McpPermission(Read)]
    [Description("Before publishing: what a publication would put live, who changed each part, what would refuse it, and " +
        "who would see it. Pass page_id for a Landing Page or a language version (or a page stored as a file, with store_id), " +
        "multilanguage_page_id for every language version with a draft, or theme true with store_id for the theme's drafts. " +
        "The answer comes from the dry run of the publication itself: parts (each with its changes section by section and " +
        "who changed it), findings (critical ones block the publication: no draft, a page document that is refused, published " +
        "pages it would replace, a palette below WCAG 2.2 AA, no schema catalog; warnings: hidden page, ended dates, an address " +
        "another page serves, a language version that publishing every language skips), affected (the audience) and the next step. Pass unpublish true to check taking a page off " +
        "instead. Changes nothing. To publish, use publish_page or publish_theme.")]
    public async Task<PublishReadiness> GetPublishReadiness(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of a Landing Page or of a language version (search_site_pages), or the path of a page stored as a file " +
            "('pages/about.page') with store_id.")] string page_id = null,
        [Description("Id of a Multilanguage Page: every language version with a draft.")] string multilanguage_page_id = null,
        [Description("true for the store's theme: every draft of its templates, rules, header, footer, brand and palette. Needs store_id.")] bool theme = false,
        [Description("Store id: needed with theme and with a page stored as a file.")] string store_id = null,
        [Description("true to check unpublishing page_id instead of publishing it.")] bool unpublish = false,
        CancellationToken cancellationToken = default)
    {
        var targets = new[] { !string.IsNullOrWhiteSpace(page_id), !string.IsNullOrWhiteSpace(multilanguage_page_id), theme }.Count(x => x);
        if (targets != 1)
        {
            throw new McpException("Pass one of page_id, multilanguage_page_id or theme true.");
        }

        if (unpublish && string.IsNullOrWhiteSpace(page_id))
        {
            throw new McpException("unpublish goes with page_id: a theme is not unpublished, and a Multilanguage Page is unpublished one language version at a time.");
        }

        PublishReadiness readiness;
        if (theme)
        {
            readiness = await ReadThemeAsync(context.User, store_id, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(multilanguage_page_id))
        {
            readiness = await ReadMultilanguagePageAsync(context.User, multilanguage_page_id, cancellationToken);
        }
        else if (PageLookup.IsFilePagePath(page_id))
        {
            readiness = await ReadFilePageAsync(context.User, store_id, page_id, unpublish, cancellationToken);
        }
        else
        {
            readiness = await ReadPageAsync(context.User, page_id, unpublish, cancellationToken);
        }

        readiness.YouCanPublish = (await authorizationService.AuthorizeAsync(context.User, Publish)).Succeeded;
        readiness.Findings = readiness.Findings.OrderBySeverity();

        return readiness;
    }

    private async Task<PublishReadiness> ReadThemeAsync(ClaimsPrincipal user, string storeId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeId))
        {
            throw new McpException("Pass store_id: the theme belongs to a store.");
        }

        await authorizationService.EnsureStoreAllowedAsync(user, storeId);
        var pages = await pageListService.SearchAsync(new SiteBuilderPageSearchCriteria { StoreId = storeId, Take = 0 }, cancellationToken);

        var readiness = new PublishReadiness
        {
            Target = "theme",
            Action = PublishAction,
            ObjectId = storeId,
            ObjectName = $"The theme of store '{storeId}'",
            StoreId = storeId,
            Affected = $"Every page of store '{storeId}' changes its look at once: the {pages.TotalCount} pages of the pages list and every " +
                "product and category page. The publication takes every draft below, whoever saved it.",
            NextStep = "publish_theme with dryRun, then again with its expected_versions; a second person approves the publication.",
        };

        await dryRuns.CheckThemeAsync(user, storeId, readiness, cancellationToken);

        return readiness;
    }

    private async Task<PublishReadiness> ReadPageAsync(ClaimsPrincipal user, string pageId, bool unpublish, CancellationToken cancellationToken)
    {
        var group = await groupedPageService.LoadPageAsync(authorizationService, user, pageId);
        var readiness = new PublishReadiness
        {
            Target = "page",
            Action = unpublish ? UnpublishAction : PublishAction,
            ObjectId = group.Id,
            ObjectName = group.Name,
            StoreId = group.StoreId,
            Affected = PageAudience.Describe(group),
            NextStep = unpublish
                ? "unpublish_page with dryRun, then without it and with the dry run's expected_versions; a second person approves it."
                : "publish_page with dryRun, then without it and with the dry run's expected_versions; a second person approves it.",
        };

        if (await dryRuns.CheckPageAsync(user, group, unpublish, readiness, cancellationToken) && !unpublish)
        {
            readiness.Parts.Add(await ReadPagePartAsync(group, readiness, cancellationToken));
            await AddPageFindingsAsync(group, readiness, cancellationToken);
        }

        return readiness;
    }

    private async Task<PublishReadiness> ReadMultilanguagePageAsync(ClaimsPrincipal user, string multilanguagePageId, CancellationToken cancellationToken)
    {
        var root = await multilanguagePageService.LoadMultilanguagePageAsync(authorizationService, user, multilanguagePageId);
        var readiness = new PublishReadiness
        {
            Target = "multilanguage_page",
            Action = PublishAction,
            ObjectId = root.Id,
            ObjectName = root.Name,
            StoreId = root.StoreId,
            NextStep = "publish_page with multilanguage_page_id and all_languages (or languages), with dryRun first, then with its expected_versions; " +
                "a second person approves it.",
        };

        var audiences = new List<string>();
        foreach (var groupId in await dryRuns.CheckMultilanguagePageAsync(user, root, readiness, cancellationToken))
        {
            var group = await groupedPageService.GetByIdAsync(groupId);
            if (group != null && await dryRuns.CheckLanguageVersionAsync(user, group, readiness, cancellationToken))
            {
                readiness.Parts.Add(await ReadPagePartAsync(group, readiness, cancellationToken));
                await AddPageFindingsAsync(group, readiness, cancellationToken);
                audiences.Add(PageAudience.Describe(group));
            }
        }

        readiness.Affected = audiences.Count == 0 ? "Nobody: nothing would be published." : string.Join(" ", audiences);

        return readiness;
    }

    private async Task<PublishReadiness> ReadFilePageAsync(ClaimsPrincipal user, string storeId, string pageId, bool unpublish, CancellationToken cancellationToken)
    {
        var page = await FilePages.LoadAsync(authorizationService, user, storeId, pageId);
        var readiness = new PublishReadiness
        {
            Target = "file_page",
            Action = unpublish ? UnpublishAction : PublishAction,
            ObjectId = page.PageId,
            ObjectName = page.PageId,
            StoreId = page.StoreId,
            Affected = $"Every customer of store '{page.StoreId}' who opens the page '{page.Path}': a page stored as a file has no audience settings.",
            NextStep = $"{(unpublish ? "unpublish_page" : "publish_page")} with page_id '{page.PageId}', store_id and dryRun first, then with its " +
                "expected_versions and draft_owner; a second person approves it.",
        };

        await dryRuns.CheckFilePageAsync(user, page, unpublish, readiness, cancellationToken);

        return readiness;
    }

    private async Task<PublishPart> ReadPagePartAsync(GroupedSiteBuilderPage group, PublishReadiness readiness, CancellationToken cancellationToken)
    {
        var draft = GroupedPagePublishing.GetDraft(group) ?? new SiteBuilderPage();
        var part = new PublishPart
        {
            ObjectType = ChangePreview.Page,
            ObjectId = group.Id,
            Name = group.Name,
            Language = group.CultureName,
            ChangedDate = draft.ModifiedDate ?? draft.CreatedDate,
        };

        AddAuthor(part, draft.ModifiedBy ?? draft.CreatedBy);
        foreach (var author in await GetAuthorsSincePublicationAsync(group, readiness, cancellationToken))
        {
            AddAuthor(part, author);
        }

        var before = group.Pages.Any(x => x.Status == PageStatuses.Published) ? await ReadContentAsync(group, draft: false, cancellationToken) : null;
        var after = await ReadContentAsync(group, draft: true, cancellationToken);
        part.Changes = PublishPart.Compare(before, after);

        return part;
    }

    private async Task<string> ReadContentAsync(GroupedSiteBuilderPage group, bool draft, CancellationToken cancellationToken)
    {
        try
        {
            return (await pageContentService.LoadGroupContentAsync(group, draft, cancellationToken)).Content;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private async Task<IList<string>> GetAuthorsSincePublicationAsync(GroupedSiteBuilderPage group, PublishReadiness readiness, CancellationToken cancellationToken)
    {
        ContentVersionPage page;
        try
        {
            page = await contentHistory.ListAsync(group.StoreId, [new HistoryItem(HistoryItemType.Page, group.Id)], cursor: null, HistoryVersions, cancellationToken);
        }
        catch (GitContentException ex)
        {
            readiness.Findings.Add(PublicationDryRuns.Info("history-unavailable", $"Who changed page '{group.Name}' since its publication could not be read.",
                GitContentFailures.Describe(logger, ex), "The draft's last author is listed; check the history in the designer.", "history", group));
            return [];
        }

        return (page?.Versions ?? [])
            .TakeWhile(x => !x.Published)
            .Select(x => x.Author?.Name ?? x.Author?.Login)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToList();
    }

    private static void AddAuthor(PublishPart part, string author)
    {
        if (!string.IsNullOrEmpty(author) && !part.ChangedBy.Contains(author, StringComparer.OrdinalIgnoreCase))
        {
            part.ChangedBy.Add(author);
        }
    }

    private async Task AddPageFindingsAsync(GroupedSiteBuilderPage group, PublishReadiness readiness, CancellationToken cancellationToken)
    {
        foreach (var finding in PageAudience.GetFindings(group, DateTime.UtcNow))
        {
            readiness.Findings.Add(finding);
        }

        var usage = await pageListService.FindPermalinkUsageAsync(new PermalinkUsageRequest
        {
            StoreId = group.StoreId,
            Permalink = group.Permalink,
            CultureNames = string.IsNullOrEmpty(group.CultureName) ? [] : [group.CultureName],
            ExcludeGroupIds = [group.Id],
            ExcludeMultilanguagePageId = group.MultilanguagePageId,
        }, cancellationToken);

        if (usage.Count > 0)
        {
            readiness.Findings.Add(new McpFinding
            {
                Id = "permalink-shared",
                Severity = McpFindingSeverities.Warning,
                Title = $"Another page serves the address '{group.Permalink}' in {group.CultureName}, so the storefront picks one of them.",
                Evidence = string.Join("; ", usage.Select(x => $"page '{x.GroupId}' ({x.Name}, {x.CultureName})")),
                Fix = "Give one of the pages another address with change_page_settings, or archive the other page in the designer.",
                Category = "address",
                ObjectType = ChangePreview.Page,
                ObjectId = group.Id,
                ObjectName = group.Name,
            });
        }
    }
}

public static class PageAudience
{
    public static string Describe(GroupedSiteBuilderPage group)
    {
        var parts = new List<string> { $"Customers of store '{group.StoreId}' who open '{group.Permalink}' in {group.CultureName ?? "the store's language"}" };
        if (!string.IsNullOrEmpty(group.UserGroups))
        {
            parts.Add($"only in the user groups {group.UserGroups}");
        }

        if (!string.IsNullOrEmpty(group.OrganizationId))
        {
            parts.Add($"only of organization '{group.OrganizationId}'");
        }

        if (group.StartDate != null || group.EndDate != null)
        {
            parts.Add($"from {Format(group.StartDate, "now")} until {Format(group.EndDate, "no end date")} (UTC)");
        }

        return string.Join(", ", parts) + (group.Visibility ? "." : "; the page is hidden (visibility off), so nobody sees it.");
    }

    public static IList<McpFinding> GetFindings(GroupedSiteBuilderPage group, DateTime now)
    {
        var findings = new List<McpFinding>();
        if (!group.Visibility)
        {
            findings.Add(PublicationDryRuns.Warning("page-hidden", $"Page '{group.Name}' is hidden, so customers do not see it once published.",
                "visibility is off.", "Turn visibility on with change_page_settings (visible true), if customers should see it.", "audience", group));
        }

        if (group.EndDate < now)
        {
            findings.Add(PublicationDryRuns.Warning("page-ended", $"The end date of page '{group.Name}' has passed, so customers do not see it once published.",
                $"End date {Format(group.EndDate, null)} UTC, now {Format(now, null)} UTC.", "Move or clear the end date with change_page_settings.", "audience", group));
        }

        if (group.StartDate > now)
        {
            findings.Add(PublicationDryRuns.Info("page-scheduled", $"Page '{group.Name}' is shown only from its start date.",
                $"Start date {Format(group.StartDate, null)} UTC.", "Nothing to do, unless it should be shown at once (change_page_settings).", "audience", group));
        }

        return findings;
    }

    private static string Format(DateTime? date, string none) => date?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? none;
}

public sealed class PublishReadiness
{
    public string Target { get; init; }

    public string Action { get; init; }

    public string ObjectId { get; init; }

    public string ObjectName { get; init; }

    public string StoreId { get; init; }

    public bool Ready => !Findings.Any(x => x.Severity == McpFindingSeverities.Critical);

    public IList<PublishPart> Parts { get; init; } = [];

    public IList<McpFinding> Findings { get; set; } = [];

    public string Affected { get; set; }

    public bool YouCanPublish { get; set; }

    public string NextStep { get; init; }
}

public sealed class PublishPart
{
    public string ObjectType { get; init; }

    public string ObjectId { get; init; }

    public string Name { get; init; }

    public string Language { get; init; }

    public IList<string> ChangedBy { get; init; } = [];

    public DateTime? ChangedDate { get; init; }

    public IList<PartChange> Changes { get; set; } = [];

    public static PublishPart FromPreview(SiteBuilderObjectChange change)
    {
        var part = new PublishPart
        {
            ObjectType = change.ObjectType,
            ObjectId = change.ObjectId,
            ChangedDate = change.ChangedDate,
            Changes = change.Fields.Select(x => new PartChange(x.Name, x.From, x.To)).ToList(),
        };

        var author = change.ChangedBy?.Name ?? change.ChangedBy?.Login;
        if (!string.IsNullOrEmpty(author))
        {
            part.ChangedBy.Add(author);
        }

        return part;
    }

    public static IList<PartChange> Compare(string published, string draft)
    {
        var after = PageDocumentEditor.Read(draft);
        if (after == null)
        {
            return [new PartChange("content", published == null ? null : "published content", "the draft is not a page document")];
        }

        var before = PageDocumentEditor.Read(published) ?? PageDocumentEditor.Read("""{ "settings": {}, "content": [] }""");

        return PageDocumentChanges.Compare(before, after).Select(x => new PartChange(x.Field, x.From, x.To)).ToList();
    }
}

public sealed record PartChange(string Field, string From, string To);
