using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.MCP.Jobs;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// The agent's translations of many pages saved into their drafts, one save per page, by a background job. A
// translation is marked up to date only while the source is the publication it was made from.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class TranslationTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    PageDraftJobRunner runner,
    TranslationSources translationSources,
    IMcpJobDispatcher jobDispatcher)
{
    private const int MaxItems = 1000;
    private const int MaxPages = 200;
    private const int MaxPreviewPages = 10;

    [McpServerTool(Name = "set_translations", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [Description("Save texts you translated into the drafts of database pages: per item a page, its version_token, a " +
        "section (or none for a page setting such as seoInfo.pageTitle), the setting path, the language and the text. " +
        "A setting that holds a text per language gets that language's text; the other languages stay. Read each page " +
        "with search_site_pages (page_id) first. Call it with dryRun first: every item is checked and the first pages " +
        "show the change. The real call starts a background job (follow it with get_job_status) that saves each page " +
        "once; a page saved by someone after you read it is skipped. With mark_up_to_date, each translated language " +
        "version is also marked up to date, but only if its source language version has not been published again since " +
        "the source_revision_date you pass. Customers see the texts only when the drafts are published.")]
    public async Task<object> SetTranslations(
        RequestContext<CallToolRequestParams> context,
        [Description("The texts, one per setting and language.")] TranslationItem[] translations,
        [Description("true to mark each translated language version up to date with its source after saving it.")] bool mark_up_to_date = false,
        [Description("true to check every item and show the change without saving it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var pages = Group(translations);
        var preview = new TranslationPreview
        {
            Action = "set_translations",
            ObjectType = "pages",
            ObjectName = $"{translations.Length} texts on {pages.Count} pages",
            DryRun = dryRun,
            PageCount = pages.Count,
            TextCount = translations.Length,
        };

        foreach (var page in pages)
        {
            await CheckAsync(context.User, page, mark_up_to_date, preview, cancellationToken);
        }

        if (pages.Count > MaxPreviewPages)
        {
            preview.AddWarning($"The change is shown on the first {MaxPreviewPages} of {pages.Count} pages.");
        }

        if (dryRun)
        {
            return preview;
        }

        var problems = preview.Pages.Where(x => x.Problem != null).ToList();
        if (problems.Count > 0)
        {
            throw new McpException($"{problems.Count} pages cannot take their translations, so nothing was saved: " +
                string.Join("; ", problems.Take(10).Select(x => $"{x.PageId}: {x.Problem}")) + ". Fix them, or leave those pages out, and call again.");
        }

        var payload = new SetTranslationsJobPayload
        {
            UserId = context.User.GetUserId(),
            MarkUpToDate = mark_up_to_date,
            Pages = pages,
        };

        return await jobDispatcher.EnqueueAsync<SetTranslationsJob>(context, payload, new McpJobRequest
        {
            Title = $"Save {translations.Length} translated texts on {pages.Count} pages",
            TotalCount = pages.Count,
            Preview = preview.Pages.Take(MaxPreviewPages).ToList(),
        }, "save them page by page with edit_page_draft.");
    }

    private static List<SetTranslationsJobPage> Group(TranslationItem[] translations)
    {
        if (translations == null || translations.Length == 0)
        {
            throw new McpException("Pass translations: the texts to save.");
        }

        if (translations.Length > MaxItems)
        {
            throw new McpException($"Pass at most {MaxItems} texts in one call.");
        }

        var pages = new List<SetTranslationsJobPage>();
        for (var i = 0; i < translations.Length; i++)
        {
            var item = translations[i] ?? throw new McpException($"Translation {i + 1} is empty.");
            if (string.IsNullOrWhiteSpace(item.PageId) || string.IsNullOrWhiteSpace(item.VersionToken) || string.IsNullOrWhiteSpace(item.SettingPath) ||
                string.IsNullOrWhiteSpace(item.Language) || item.Text == null)
            {
                throw new McpException($"Translation {i + 1} needs page_id, version_token, setting_path, language and text.");
            }

            if (!SiteBuilderPageItems.IsLanguageCode(item.Language))
            {
                throw new McpException($"Translation {i + 1}: '{item.Language}' is not a language code such as fr-FR.");
            }

            var page = pages.FirstOrDefault(x => x.PageId == item.PageId);
            if (page == null)
            {
                page = new SetTranslationsJobPage { PageId = item.PageId, VersionToken = item.VersionToken, SourceRevisionDate = item.SourceRevisionDate };
                pages.Add(page);
            }
            else if (page.VersionToken != item.VersionToken || page.SourceRevisionDate != item.SourceRevisionDate)
            {
                throw new McpException($"Translation {i + 1}: the texts of page '{item.PageId}' name different version_token or source_revision_date values; " +
                    "pass the ones of the version you read.");
            }

            page.Texts.Add(new SetTranslationsJobText
            {
                SectionId = string.IsNullOrWhiteSpace(item.SectionId) ? null : item.SectionId,
                SettingPath = item.SettingPath.Trim(),
                Language = item.Language.Trim(),
                Text = item.Text,
            });
        }

        if (pages.Count > MaxPages)
        {
            throw new McpException($"Pass the texts of at most {MaxPages} pages in one call.");
        }

        return pages;
    }

    // Each page in the user's stores, at the version the agent read, taking its texts as the designer's save would.
    private async Task CheckAsync(ClaimsPrincipal user, SetTranslationsJobPage page, bool markUpToDate, TranslationPreview preview, CancellationToken cancellationToken)
    {
        var group = await groupedPageService.GetByIdAsync(page.PageId)
            ?? throw new McpException($"Page '{page.PageId}' was not found. Call search_site_pages to find its id.");

        // A page of another store is refused before anything about it is said.
        await authorizationService.EnsureAllowedAsync(user, group, group.StoreId);

        var result = await runner.RunAsync(user, page.PageId, page.VersionToken, TranslationOperations.Create(page.Texts), dryRun: true, cancellationToken);
        var problem = result.Outcome switch
        {
            PageJobOutcome.Changed or PageJobOutcome.Unchanged => null,
            PageJobOutcome.Skipped => "it changed since you read it; read it again with search_site_pages (page_id) and translate that version",
            _ => result.Reason,
        };

        string markProblem = null;
        if (problem == null && markUpToDate)
        {
            markProblem = await translationSources.GetProblemAsync(group, page.SourceRevisionDate, cancellationToken);
        }

        var label = $"page '{group.Name}' ({group.Id}, {group.CultureName})";
        if (preview.Pages.Count < MaxPreviewPages)
        {
            foreach (var change in result.Changes ?? [])
            {
                preview.AddChange($"{label}: {change.Field}", change.From, change.To);
            }
        }

        if (problem != null)
        {
            preview.AddWarning($"{label}: {problem}.");
        }
        else if (markProblem != null)
        {
            preview.AddWarning($"{label} will be saved but not marked up to date: {markProblem}.");
        }

        preview.Pages.Add(new TranslationPageRow
        {
            PageId = group.Id,
            Name = group.Name,
            Language = group.CultureName,
            TextCount = page.Texts.Count,
            Problem = problem,
            NotMarkedUpToDate = markProblem,
        });
    }
}

public sealed class TranslationItem
{
    [JsonPropertyName("page_id")]
    [Description("The language version (or Landing Page) to save the text in.")]
    public string PageId { get; set; }

    [JsonPropertyName("version_token")]
    [Description("version_token of the page as search_site_pages (page_id) returned it.")]
    public string VersionToken { get; set; }

    [JsonPropertyName("section_id")]
    [Description("The section the setting belongs to; omit it for a setting of the page, such as seoInfo.pageTitle.")]
    public string SectionId { get; set; }

    [JsonPropertyName("setting_path")]
    [Description("The setting: a name or a dotted path, such as 'title', 'button.label' or 'blocks.b1.text'.")]
    public string SettingPath { get; set; }

    [JsonPropertyName("language")]
    [Description("Language of the text, for example fr-FR.")]
    public string Language { get; set; }

    [JsonPropertyName("text")]
    [Description("The translated text.")]
    public string Text { get; set; }

    [JsonPropertyName("source_revision_date")]
    [Description("With mark_up_to_date: source_revision_date of the source language version you translated from, as search_site_pages (page_id) returned it.")]
    public DateTime? SourceRevisionDate { get; set; }
}

public sealed class TranslationPreview : McpChangePreview
{
    [JsonPropertyName("page_count")]
    public int PageCount { get; set; }

    [JsonPropertyName("text_count")]
    public int TextCount { get; set; }

    [JsonPropertyName("pages")]
    public IList<TranslationPageRow> Pages { get; set; } = [];
}

public sealed class TranslationPageRow
{
    [JsonPropertyName("page_id")]
    public string PageId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("language")]
    public string Language { get; init; }

    [JsonPropertyName("text_count")]
    public int TextCount { get; init; }

    // Why the page cannot take its texts; a real call is refused while any page has one.
    [JsonPropertyName("problem")]
    public string Problem { get; init; }

    // Why the page would be saved but not marked up to date.
    [JsonPropertyName("not_marked_up_to_date")]
    public string NotMarkedUpToDate { get; init; }
}
