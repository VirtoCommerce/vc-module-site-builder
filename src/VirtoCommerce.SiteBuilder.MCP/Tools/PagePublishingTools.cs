using System.ComponentModel;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// Publishing and unpublishing change what customers see, so every call waits for a second person. The approver sees
// the dry run of the requester; the change then runs as the approver, through the endpoint's own operation.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class PagePublishingTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IMultilanguagePageService multilanguagePageService,
    IGroupedPageOperationService groupedPageOperationService,
    IMultilanguagePageOperationService multilanguagePageOperationService)
{
    [McpServerTool(Name = "publish_page", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Publish)]
    [McpRequiresApproval]
    [Description("Publish the draft of a database page, so customers see it; the draft replaces the published version. " +
        "Every call waits for a second person's approval, who sees what the dry run shows. Pass page_id for a Landing " +
        "Page or one language version, or multilanguage_page_id with languages (or all_languages) for language versions " +
        "of a Multilanguage Page. The page is shown to its audience between its start and end dates, which " +
        "change_page_settings sets. Returns what is (or would be) published. Not available for pages stored as files yet.")]
    public async Task<SiteBuilderChange> PublishPage(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of a Landing Page or of a language version (search_site_pages). Pass it or multilanguage_page_id.")] string page_id = null,
        [Description("Id of a Multilanguage Page whose language versions to publish.")] string multilanguage_page_id = null,
        [Description("With multilanguage_page_id: language codes of the versions to publish, for example ['fr-FR']; each must have a draft.")] string[] languages = null,
        [Description("With multilanguage_page_id: true to publish every language version that has a draft.")] bool all_languages = false,
        [Description("true to return what would be published, without publishing it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(page_id) == !string.IsNullOrEmpty(multilanguage_page_id))
        {
            throw new McpException("Pass either page_id or multilanguage_page_id.");
        }

        if (!string.IsNullOrEmpty(page_id))
        {
            if (languages?.Length > 0 || all_languages)
            {
                throw new McpException("languages and all_languages go with multilanguage_page_id; page_id is one page already.");
            }

            return await PublishGroupAsync(context.User, page_id, publish: true, dryRun, cancellationToken);
        }

        if (all_languages == languages?.Length > 0)
        {
            throw new McpException("With multilanguage_page_id, pass either languages or all_languages: true.");
        }

        var root = await multilanguagePageService.LoadMultilanguagePageAsync(authorizationService, context.User, multilanguage_page_id);

        return all_languages
            ? await PublishAllLanguagesAsync(context.User, root, dryRun, cancellationToken)
            : await PublishLanguagesAsync(context.User, root, languages, dryRun, cancellationToken);
    }

    [McpServerTool(Name = "unpublish_page", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Publish)]
    [McpRequiresApproval]
    [Description("Take a published database page off the storefront: its published version becomes the draft. Every call " +
        "waits for a second person's approval. Refused while the page has unpublished changes: publish them " +
        "(publish_page) or discard them (discard_draft) first. For a Multilanguage Page, unpublish each language " +
        "version by its page_id. Not available for pages stored as files yet.")]
    public Task<SiteBuilderChange> UnpublishPage(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of a Landing Page or of a language version (search_site_pages).")] string page_id,
        [Description("true to return what would be unpublished, without unpublishing it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        return PublishGroupAsync(context.User, page_id, publish: false, dryRun, cancellationToken);
    }

    private async Task<SiteBuilderChange> PublishGroupAsync(ClaimsPrincipal user, string pageId, bool publish, bool dryRun, CancellationToken cancellationToken)
    {
        var group = await groupedPageService.LoadPageAsync(authorizationService, user, pageId);
        var change = new SiteBuilderChange
        {
            Action = publish ? "publish_page" : "unpublish_page",
            ObjectType = ChangePreview.Page,
            ObjectId = group.Id,
            ObjectName = group.Name,
            PageId = group.Id,
            MultilanguagePageId = group.MultilanguagePageId,
        };

        await SiteBuilderChanges.RunAsync(change,
            isDryRun => RunPublishAsync(user, group.Id, publish, isDryRun, cancellationToken),
            x => EnsurePublished(x, group, publish),
            dryRun);

        return change;
    }

    private async Task<SiteBuilderChange> PublishAllLanguagesAsync(ClaimsPrincipal user, MultilanguagePage root, bool dryRun, CancellationToken cancellationToken)
    {
        var change = NewMultilanguageChange(root);
        var (_, result) = await SiteBuilderChanges.RunAsync(change,
            isDryRun => multilanguagePageOperationService.PublishAllAsync(new MultilanguagePageOperationRequest { MultilanguagePageId = root.Id, DryRun = isDryRun },
                user, cancellationToken),
            x => SiteBuilderChanges.EnsureSucceeded(x, $"Multilanguage Page '{root.Id}'", PageLookup.ForbiddenMultilanguagePage(root)),
            dryRun);

        foreach (var skipped in result.Value?.Skipped ?? [])
        {
            change.AddWarning(skipped.Reason == MultilanguagePageBatchResult.NoDraftReason
                ? $"{skipped.CultureName}: no draft, nothing to publish."
                : $"{skipped.CultureName}: publishing failed; the version is as it was. Try it again with languages: ['{skipped.CultureName}'].");
        }

        return change;
    }

    // Every chosen version is checked before the first is published. The source language goes first, as in publish-all:
    // translations published with it are based on its new revision.
    private async Task<SiteBuilderChange> PublishLanguagesAsync(ClaimsPrincipal user, MultilanguagePage root, string[] languages, bool dryRun,
        CancellationToken cancellationToken)
    {
        var versions = (await multilanguagePageService.GetLanguagesAsync(root.Id, cancellationToken))
            .Where(x => x.Page != null)
            .ToDictionary(x => x.CultureName, x => x.Page, StringComparer.OrdinalIgnoreCase);

        var missing = languages.Where(x => !versions.ContainsKey(x)).ToList();
        if (missing.Count > 0)
        {
            throw new McpException($"Multilanguage Page '{root.Id}' has no version in {string.Join(", ", missing)}. create_page with kind 'language_version' adds one.");
        }

        var groups = languages
            .Select(x => versions[x])
            .DistinctBy(x => x.Id)
            .OrderByDescending(x => string.Equals(x.CultureName, root.DefaultCultureName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var change = NewMultilanguageChange(root);
        change.DryRun = true;
        foreach (var group in groups)
        {
            var check = await RunPublishAsync(user, group.Id, publish: true, isDryRun: true, cancellationToken);
            EnsurePublished(check, group, publish: true);
            change.AddChanges(check.Preview);
        }

        change.AffectsPublished = true;
        if (dryRun)
        {
            return change;
        }

        var published = new List<string>();
        foreach (var group in groups)
        {
            var result = await RunPublishAsync(user, group.Id, publish: true, isDryRun: false, cancellationToken);
            if (!result.Succeeded)
            {
                var done = published.Count == 0 ? "No version was published." : $"Published: {string.Join(", ", published)}.";
                throw new McpException($"Publishing the {group.CultureName} version '{group.Id}' failed: {result.Error ?? result.Status.ToString()}. {done}");
            }

            published.Add(group.CultureName);
        }

        change.DryRun = false;
        return change;
    }

    private Task<SiteBuilderOperationResult> RunPublishAsync(ClaimsPrincipal user, string groupId, bool publish, bool isDryRun, CancellationToken cancellationToken) =>
        groupedPageOperationService.PublishAsync(new GroupedPagePublishOperationRequest
        {
            GroupId = groupId,
            Publish = publish,
            SaveId = Guid.NewGuid().ToString("N"),
            DryRun = isDryRun,
        }, user, cancellationToken);

    private static void EnsurePublished(SiteBuilderOperationResult result, GroupedSiteBuilderPage group, bool publish)
    {
        if (result.Status == SiteBuilderOperationStatus.Invalid)
        {
            var hint = publish
                ? "There is no draft to publish: the page is published as it is."
                : group.Pages.All(x => x.Status != PageStatuses.Published)
                    ? "It is not published."
                    : "It has unpublished changes: publish them with publish_page or discard them with discard_draft first.";
            throw new McpException($"Page '{group.Id}' was not {(publish ? "published" : "unpublished")}. {hint}");
        }

        SiteBuilderChanges.EnsureSucceeded(result, $"Page '{group.Id}'", PageLookup.ForbiddenPage(group));
    }

    private static SiteBuilderChange NewMultilanguageChange(MultilanguagePage root) => new()
    {
        Action = "publish_page",
        ObjectType = ChangePreview.MultilanguagePage,
        ObjectId = root.Id,
        ObjectName = root.Name,
        MultilanguagePageId = root.Id,
    };
}
