using System.ComponentModel;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// What a publication would put live and what keeps it from going live, read from the dry runs of the operations that
// publish: the same checks refuse here as in publish_page and publish_theme, and nothing is written. Who changed each
// part comes from the versions the store keeps; who sees it, from the page's settings.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class PublishReadinessTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService pageContentService,
    IMultilanguagePageService multilanguagePageService,
    PublicationDryRuns dryRuns,
    ISiteBuilderPageListService pageListService,
    IContentHistory contentHistory)
{
    private const string PublishAction = "publish";
    private const string UnpublishAction = "unpublish";

    // Versions read back to find who changed a page since it was last published.
    private const int HistoryVersions = 20;

    [McpServerTool(Name = "get_publish_readiness", ReadOnly = true, Destructive = false, Idempotent = true)]
    [McpPermission(Read)]
    [Description("Before publishing: what a publication would put live, who changed each part, what would refuse it, and " +
        "who would see it. Pass page_id for a Landing Page or a language version (or a page stored as a file, with store_id), " +
        "multilanguage_page_id for every language version with a draft, or theme true with store_id for the theme's drafts. " +
        "The answer comes from the dry run of the publication itself: parts (each with its changes section by section and " +
        "who changed it), findings (critical ones block the publication: no draft, a page document that is refused, a palette " +
        "below WCAG 2.2 AA, no schema catalog; warnings: hidden page, ended dates, an address another page serves), affected " +
        "(the audience) and the next step. Pass unpublish true to check taking a page off instead. Changes nothing. To publish, " +
        "use publish_page or publish_theme.")]
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

        await authorizationService.EnsureAllowedAsync(user, new SiteBuilderPageSearchCriteria { StoreId = storeId }, storeId);
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
                ? "unpublish_page with dryRun, then without it; a second person approves it."
                : "publish_page with dryRun, then without it; a second person approves it.",
        };

        if (await dryRuns.CheckPageAsync(user, group, unpublish, readiness, cancellationToken) && !unpublish)
        {
            readiness.Parts.Add(await ReadPagePartAsync(group, cancellationToken));
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
            NextStep = "publish_page with multilanguage_page_id and all_languages (or languages), with dryRun first; a second person approves it.",
        };

        var audiences = new List<string>();
        foreach (var groupId in await dryRuns.CheckMultilanguagePageAsync(user, root, readiness, cancellationToken))
        {
            var group = await groupedPageService.GetByIdAsync(groupId);
            if (group != null)
            {
                readiness.Parts.Add(await ReadPagePartAsync(group, cancellationToken));
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
            NextStep = $"{(unpublish ? "unpublish_page" : "publish_page")} with page_id '{page.PageId}', store_id and dryRun first; a second person approves it.",
        };

        await dryRuns.CheckFilePageAsync(user, page, unpublish, readiness, cancellationToken);

        return readiness;
    }

    // The draft against what is published, section by section, with who saved the draft and, where the store keeps
    // history, everyone who saved the page since its last publication.
    private async Task<PublishPart> ReadPagePartAsync(GroupedSiteBuilderPage group, CancellationToken cancellationToken)
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
        foreach (var author in await GetAuthorsSincePublicationAsync(group, cancellationToken))
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

    private async Task<IList<string>> GetAuthorsSincePublicationAsync(GroupedSiteBuilderPage group, CancellationToken cancellationToken)
    {
        if (await contentHistory.GetStatusAsync(group.StoreId, cancellationToken) != ContentHistoryStatus.Enabled)
        {
            return [];
        }

        var page = await contentHistory.ListAsync(group.StoreId, [new HistoryItem(HistoryItemType.Page, group.Id)], cursor: null, HistoryVersions, cancellationToken);

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

    // What keeps a published page from being seen as the author expects: the page's own settings, and another page
    // that serves the same address in the language.
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

// The dry runs of the operations that publish, read into a readiness: the parts they list and their refusals as findings.
public class PublicationDryRuns(
    IGroupedPageOperationService groupedPageOperationService,
    IMultilanguagePageOperationService multilanguagePageOperationService,
    IFilePageOperationService filePageOperationService,
    IThemeOperationService themeOperationService,
    ISiteBuilderContentQueryService contentQueryService,
    ISchemaCatalogService schemaCatalogService)
{
    private const string ContrastMarker = "contrast";

    public async Task CheckThemeAsync(ClaimsPrincipal user, string storeId, PublishReadiness readiness, CancellationToken cancellationToken)
    {
        var catalog = await ThemeFiles.GetCatalogAsync(schemaCatalogService, storeId, cancellationToken);
        var catalogHash = ThemeFiles.GetCatalogHash(catalog);
        if (catalogHash == null)
        {
            readiness.Findings.Add(Blocker("schema-catalog-missing", "The storefront has not sent its schema catalog, so the theme cannot be checked or published.",
                $"Store '{storeId}' has no current schema catalog.", "Open the store once in the designer: its preview sends the catalog. Then check again.", "catalog"));
            return;
        }

        var check = await themeOperationService.PublishAsync(new ThemePublishOperationRequest { StoreId = storeId, CatalogHash = catalogHash, DryRun = true },
            user, cancellationToken);
        if (check.Status == SiteBuilderOperationStatus.Forbidden)
        {
            throw new McpForbiddenException($"Your builder:read permission does not cover store '{storeId}'.");
        }

        if (!check.Succeeded)
        {
            AddThemeRefusals(check, readiness);
            return;
        }

        foreach (var draft in check.Preview?.Objects ?? [])
        {
            readiness.Parts.Add(PublishPart.FromPreview(draft));
        }

        foreach (var warning in check.Preview?.Warnings ?? [])
        {
            readiness.Findings.Add(Warning("theme-warning", warning, "The dry run of the theme's publication.", "Review it before publishing; it does not stop the publication.", "theme"));
        }

        if (readiness.Parts.Count == 0)
        {
            readiness.Findings.Add(Info("nothing-to-publish", "The theme has no drafts: a publication would put out the live files again.",
                $"Store '{storeId}' has no theme drafts.", "Nothing to do; edit_theme_draft, edit_template or edit_template_rules make drafts.", "theme"));
        }
    }

    // true when the dry run succeeded.
    public async Task<bool> CheckPageAsync(ClaimsPrincipal user, GroupedSiteBuilderPage group, bool unpublish, PublishReadiness readiness,
        CancellationToken cancellationToken)
    {
        var check = await groupedPageOperationService.PublishAsync(new GroupedPagePublishOperationRequest
        {
            GroupId = group.Id,
            Publish = !unpublish,
            SaveId = Guid.NewGuid().ToString("N"),
            DryRun = true,
        }, user, cancellationToken);

        EnsureAllowed(check, PageLookup.ForbiddenPage(group));
        if (check.Status == SiteBuilderOperationStatus.Invalid)
        {
            readiness.Findings.Add(unpublish ? UnpublishRefusal(group) : NoDraft(group));
            return false;
        }

        SiteBuilderChanges.EnsureSucceeded(check, $"Page '{group.Id}'", PageLookup.ForbiddenPage(group));
        if (unpublish)
        {
            readiness.Parts.Add(new PublishPart { ObjectType = ChangePreview.Page, ObjectId = group.Id, Name = group.Name, Language = group.CultureName,
                Changes = [new PartChange("status", PageStatuses.Published, "draft: customers no longer see the page")] });
        }
        else if (group.Pages.Any(x => x.Status == PageStatuses.Published))
        {
            readiness.Findings.Add(Info("unpublish-refused-while-draft", "While the page has this draft, unpublish_page refuses it; publishing the draft lifts that.",
                $"Page '{group.Id}' has a published version and a draft.", "To take the page off instead, discard the draft (discard_draft) and then unpublish it.", "rules"));
        }

        return true;
    }

    // The language versions the publication of every language would publish.
    public async Task<IList<string>> CheckMultilanguagePageAsync(ClaimsPrincipal user, MultilanguagePage root, PublishReadiness readiness,
        CancellationToken cancellationToken)
    {
        var check = await multilanguagePageOperationService.PublishAllAsync(new MultilanguagePageOperationRequest { MultilanguagePageId = root.Id, DryRun = true },
            user, cancellationToken);
        EnsureAllowed(check, PageLookup.ForbiddenMultilanguagePage(root));
        SiteBuilderChanges.EnsureSucceeded(check, $"Multilanguage Page '{root.Id}'", PageLookup.ForbiddenMultilanguagePage(root));

        var groupIds = check.Preview?.Objects.Select(x => x.ObjectId).Where(x => x != null).Distinct().ToList() ?? [];
        if (groupIds.Count == 0)
        {
            readiness.Findings.Add(Blocker("no-draft", $"No language version of Multilanguage Page '{root.Name}' has a draft, so nothing would be published.",
                $"Multilanguage Page '{root.Id}': every language version is published as it is.", "Edit a language version with edit_page_draft first.", "content"));
        }

        return groupIds;
    }

    public async Task CheckFilePageAsync(ClaimsPrincipal user, FilePage page, bool unpublish, PublishReadiness readiness, CancellationToken cancellationToken)
    {
        var check = await FilePages.RunAsync(() => unpublish
            ? filePageOperationService.UnpublishAsync(new FilePageOperationRequest { StoreId = page.StoreId, ContentType = page.ContentType, Path = page.Path, DryRun = true },
                user, cancellationToken)
            : filePageOperationService.PublishAsync(new FilePagePublishOperationRequest { StoreId = page.StoreId, ContentType = page.ContentType, Path = page.Path, DryRun = true },
                user, cancellationToken));

        EnsureAllowed(check, FilePages.Forbidden(page, unpublish ? "unpublish" : "publish"));
        if (!check.Succeeded)
        {
            readiness.Findings.Add(FilePageRefusal(check, page, unpublish));
            return;
        }

        foreach (var item in check.Preview?.Objects ?? [])
        {
            var part = PublishPart.FromPreview(item);
            if (!unpublish)
            {
                part.Changes = PublishPart.Compare(await FilePages.ReadAsync(contentQueryService, user, page, draft: false, cancellationToken),
                    await FilePages.ReadAsync(contentQueryService, user, page, draft: true, cancellationToken));
            }

            readiness.Parts.Add(part);
        }

        foreach (var warning in check.Preview?.Warnings ?? [])
        {
            readiness.Findings.Add(Info("git-flow", warning, "The dry run of the publication.", "Nothing to do before publishing.", "publishing"));
        }
    }

    private static void EnsureAllowed(SiteBuilderOperationResult result, string forbidden)
    {
        if (result.Status == SiteBuilderOperationStatus.Forbidden)
        {
            throw new McpForbiddenException(forbidden);
        }
    }

    private static void AddThemeRefusals(SiteBuilderOperationResult check, PublishReadiness readiness)
    {
        var errors = check.Errors?.Count > 0 ? check.Errors : [check.Error ?? check.Status.ToString()];
        foreach (var error in errors)
        {
            readiness.Findings.Add(ThemeRefusal(error));
        }
    }

    private static McpFinding ThemeRefusal(string error)
    {
        if (error.Contains(ContrastMarker, StringComparison.OrdinalIgnoreCase))
        {
            return Blocker("palette-contrast", "A color pair of the palette is below the WCAG 2.2 AA contrast, so the theme cannot be published.", error,
                "Fix the palette with edit_theme_draft (part palette): regenerate it or change the failing colors.", "contrast");
        }

        return Blocker("theme-refused", "The theme's publication would be refused.", error, "Fix the draft it names, then check again.", "validation");
    }

    private static McpFinding NoDraft(GroupedSiteBuilderPage group) =>
        Blocker("no-draft", $"Page '{group.Name}' has no draft, so publishing it would change nothing.", $"Page '{group.Id}' is {group.Status} without unpublished changes.",
            "Edit it with edit_page_draft first, or there is nothing to publish.", "content", group);

    private static McpFinding UnpublishRefusal(GroupedSiteBuilderPage group)
    {
        var published = group.Pages.Any(x => x.Status == PageStatuses.Published);
        if (!published)
        {
            return Blocker("not-published", $"Page '{group.Name}' is not published, so there is nothing to unpublish.", $"Page '{group.Id}' is {group.Status}.",
                "Nothing to do.", "rules", group);
        }

        return Blocker("unpublish-refused-while-draft", $"Page '{group.Name}' has unpublished changes, and a page with a draft cannot be unpublished.",
            $"Page '{group.Id}' has a published version and a draft.", "Publish the draft (publish_page) or discard it (discard_draft) first, then unpublish.", "rules", group);
    }

    private static McpFinding FilePageRefusal(SiteBuilderOperationResult check, FilePage page, bool unpublish)
    {
        if (check.Status == SiteBuilderOperationStatus.NotFound)
        {
            return unpublish
                ? Blocker("not-published", $"Page '{page.PageId}' is not published, so there is nothing to unpublish.", $"Store '{page.StoreId}'.", "Nothing to do.", "rules")
                : Blocker("page-not-found", $"Page '{page.PageId}' was not found in store '{page.StoreId}'.", "No draft or published file.", "Check the folder and path the designer shows.", "content");
        }

        var details = check.Errors?.Count > 0 ? string.Join("; ", check.Errors) : check.Error;

        return Blocker("page-refused", $"The publication of page '{page.PageId}' would be refused.", details ?? check.Status.ToString(),
            "Fix the page with edit_page_draft or in the designer, then check again.", "validation");
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

// Who sees a page of the database once it is published, and the settings that hide it from them.
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
    // page, multilanguage_page, file_page or theme.
    public string Target { get; init; }

    // publish or unpublish.
    public string Action { get; init; }

    public string ObjectId { get; init; }

    public string ObjectName { get; init; }

    public string StoreId { get; init; }

    // No critical finding: the publication would go through.
    public bool Ready => !Findings.Any(x => x.Severity == McpFindingSeverities.Critical);

    // What would go live, each part with its changes and who made them.
    public IList<PublishPart> Parts { get; init; } = [];

    // Critical ones block the publication; warnings and notes do not.
    public IList<McpFinding> Findings { get; set; } = [];

    // Who would see the change.
    public string Affected { get; set; }

    // You hold builder:publish; the publication still waits for a second person's approval.
    public bool YouCanPublish { get; set; }

    public string NextStep { get; init; }
}

public sealed class PublishPart
{
    public string ObjectType { get; init; }

    public string ObjectId { get; init; }

    public string Name { get; init; }

    public string Language { get; init; }

    // Who changed it since it was last published, as the store records it; empty where nobody is recorded.
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

    // What is published against what would be, section by section; everything is new for a page never published.
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
