using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// Where the storefront shows a page or a template, and which template the theme's rules give a page, a product or a
// category. The template is a calculation on the server from the rules file, not the storefront's rendering: the server
// evaluates only the conditions whose data it holds and says which it did not.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class PreviewTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService pageContentService,
    ISiteBuilderPreviewQueryService previewQueryService,
    ISiteBuilderContentQueryService contentQueryService,
    IDesignerOperationService designerOperationService,
    IThemeStorageFactory themeStorageFactory)
{
    private const string CalculationNote = "Calculated on the server from the theme's template rules; this is not a rendering. The storefront " +
        "picks the template when it shows the page, and may differ where a condition was not evaluated here.";

    [McpServerTool(Name = "get_preview", ReadOnly = true, Destructive = false, Idempotent = true)]
    [McpPermission(Read)]
    [Description("Where to see a page or a template on the storefront, and which template the theme's rules give it and why. " +
        "page_id: the storefront's preview of the page's draft, and the template its rules pick, evaluated on the page's path, " +
        "language, source and settings. template_key: the storefront page that shows the template (the published theme; a draft " +
        "of a template shows only in the designer), and the rules that give it pages. product_id or category_id: the rules in " +
        "order, with the conditions the server cannot evaluate for a product or category (categories, product type, properties) " +
        "marked as not evaluated. The template choice is the server's calculation, not a rendering.")]
    public async Task<PreviewAnswer> GetPreview(
        RequestContext<CallToolRequestParams> context,
        [Description("A database page: a Landing Page or a language version (search_site_pages).")] string page_id = null,
        [Description("Store id; required with template_key, product_id and category_id.")] string store_id = null,
        [Description("A template of the store's theme, for example 'product.bicycles'.")] string template_key = null,
        [Description("A product of the store's catalog.")] string product_id = null,
        [Description("A category of the store's catalog.")] string category_id = null,
        [Description("'published' (default) to calculate with the published rules and templates, 'draft' with the theme's drafts.")] string theme = null,
        CancellationToken cancellationToken = default)
    {
        var named = new[] { page_id, template_key, product_id, category_id }.Count(x => !string.IsNullOrWhiteSpace(x));
        if (named != 1)
        {
            throw new McpException("Pass one of page_id, template_key, product_id and category_id.");
        }

        var draftTheme = SitePageFilters.ParseVersion(theme ?? SitePageFilters.PublishedVersion) == false;
        if (!string.IsNullOrWhiteSpace(page_id))
        {
            return await PreviewPageAsync(context.User, page_id.Trim(), draftTheme, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(store_id))
        {
            throw new McpException("Pass store_id: templates and rules belong to a store's theme.");
        }

        await authorizationService.EnsureAllowedAsync(context.User, new SiteBuilderPageSearchCriteria { StoreId = store_id }, store_id);

        if (!string.IsNullOrWhiteSpace(template_key))
        {
            return await PreviewTemplateAsync(context.User, store_id, template_key.Trim(), draftTheme, cancellationToken);
        }

        var type = string.IsNullOrWhiteSpace(product_id) ? "category" : "product";
        return new PreviewAnswer
        {
            StoreId = store_id,
            Subject = $"{type} '{product_id ?? category_id}'",
            Template = await ChooseAsync(context.User, store_id, type, subject: null, ownTemplate: null, draftTheme, cancellationToken),
        };
    }

    private async Task<PreviewAnswer> PreviewPageAsync(ClaimsPrincipal user, string pageId, bool draftTheme, CancellationToken cancellationToken)
    {
        var group = await groupedPageService.LoadPageAsync(authorizationService, user, pageId);
        var answer = new PreviewAnswer { StoreId = group.StoreId, Subject = $"page '{group.Id}' ({group.Permalink})" };

        var link = await previewQueryService.GetGroupedPagePreviewAsync(new GroupedPageQueryRequest { GroupId = group.Id }, user, cancellationToken);
        SetLink(answer, link, "The storefront's preview of the page's draft.");

        // What customers see: the published version's settings, or the draft's for a page never published.
        var published = group.Pages.Any(x => x.Status == PageStatuses.Published);
        SiteBuilderContentSnapshot snapshot;
        try
        {
            snapshot = await pageContentService.LoadGroupContentAsync(group, draft: !published, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            throw new McpException($"Page '{pageId}' was deleted while it was read.");
        }

        var settings = SiteBuilderPageDocument.Read(snapshot.Content)?.Settings ?? [];
        var subject = new PageRuleSubject(group.Permalink, group.CultureName, settings, TemplateRuleConditions.PagesObjectType);
        answer.Template = await ChooseAsync(user, group.StoreId, "page", subject, GetString(settings["template"]), draftTheme, cancellationToken);
        answer.Template.Notes.Add($"The page's settings are those of its {(published ? "published version" : "draft, as it was never published")}.");

        return answer;
    }

    private async Task<PreviewAnswer> PreviewTemplateAsync(ClaimsPrincipal user, string storeId, string templateKey, bool draftTheme,
        CancellationToken cancellationToken)
    {
        var type = TemplateRulesDocument.PageTypeOf(templateKey);
        if (type == null || !ThemeStorage.IsTemplateKey(templateKey))
        {
            throw new McpException($"'{templateKey}' is not a template key such as 'product' or 'product.bicycles'.");
        }

        var answer = new PreviewAnswer { StoreId = storeId, Subject = $"template '{templateKey}'" };
        var link = await previewQueryService.GetTemplatePreviewAsync(new SiteBuilderTemplatePreviewRequest { StoreId = storeId, TemplateKey = templateKey },
            user, cancellationToken);
        SetLink(answer, link, "A storefront page of the template's type, with the published theme. A draft of the template shows only in the designer.",
            notFound: "The theme's schema of this page type names no sample page to show the template on.");

        var (rules, _) = await ReadRulesAsync(user, storeId, draftTheme, cancellationToken);
        answer.PickedBy = rules.RulesOf(type)
            .Where(x => TemplateRulesDocument.GetTemplate(x) == templateKey)
            .Select(x => $"{GetId(x)}: {TemplateRuleRow.Describe(x)}")
            .ToList();
        if (answer.PickedBy.Count == 0)
        {
            answer.Notes.Add(templateKey.Contains('.')
                ? $"No rule picks '{templateKey}', so no page gets it: add one with edit_template_rules."
                : $"'{templateKey}' is the default {type} template: pages get it when no rule picks another.");
        }

        return answer;
    }

    // The storefront's choice (its templates/rules.ts): the page's own template when it names one of its type that
    // exists, else the first enabled rule of the type whose template exists and whose conditions all hold, else the
    // type's default template. A rule with a condition the server cannot evaluate may or may not pick its template.
    private async Task<TemplateChoice> ChooseAsync(ClaimsPrincipal user, string storeId, string pageType, PageRuleSubject subject, string ownTemplate,
        bool draftTheme, CancellationToken cancellationToken)
    {
        var templates = (await ThemeTemplates.ListAsync(contentQueryService, user, storeId, cancellationToken))
            .Where(x => x.Published || (draftTheme && x.HasDraft))
            .Select(x => x.Key)
            .Concat(ThemeTemplates.BuiltIn)
            .ToHashSet(StringComparer.Ordinal);
        var (rules, path) = await ReadRulesAsync(user, storeId, draftTheme, cancellationToken);

        var choice = new TemplateChoice { PageType = pageType, Template = pageType, Note = CalculationNote, Theme = draftTheme ? "draft" : "published", RulesFile = path };
        if (ownTemplate != null && TemplateRulesDocument.PageTypeOf(ownTemplate) == pageType && templates.Contains(ownTemplate))
        {
            choice.Template = ownTemplate;
            choice.Reason = $"The page's settings name its own template '{ownTemplate}', which comes before every rule.";
            choice.Decided = true;
            return choice;
        }

        var undecided = new List<string>();
        foreach (var rule in rules.RulesOf(pageType))
        {
            var row = Evaluate(rule, pageType, subject, templates);
            choice.Rules.Add(row);
            if (row.Outcome == RuleOutcome.MayMatch)
            {
                undecided.Add(row.Id);
            }
            else if (row.Outcome == RuleOutcome.Matches)
            {
                choice.Template = row.Template;
                choice.RuleId = row.Id;
                break;
            }
        }

        choice.Decided = undecided.Count == 0;
        choice.NotEvaluated = choice.Rules.SelectMany(x => x.Conditions).Where(x => x.Holds == null).Select(x => x.Name).Distinct(StringComparer.Ordinal).ToList();
        var picked = choice.RuleId == null
            ? $"no rule picks a template, so it gets the default {pageType} template '{pageType}'"
            : $"rule '{choice.RuleId}' picks '{choice.Template}'";
        choice.Reason = undecided.Count == 0
            ? $"By the rules, {picked}."
            : $"Rule(s) {string.Join(", ", undecided)} come first and may pick their template, depending on conditions the server does not " +
              $"evaluate; if none of them applies, {picked}.";

        return choice;
    }

    private static RuleEvaluationRow Evaluate(JsonObject rule, string pageType, PageRuleSubject subject, ISet<string> templates)
    {
        var row = new RuleEvaluationRow { Id = GetId(rule), Template = TemplateRulesDocument.GetTemplate(rule) };
        if (TemplateRulesDocument.IsDisabled(rule))
        {
            row.Outcome = RuleOutcome.Skipped;
            row.Why = "disabled";
            return row;
        }

        if (TemplateRulesDocument.PageTypeOf(row.Template) != pageType || !templates.Contains(row.Template))
        {
            row.Outcome = RuleOutcome.Skipped;
            row.Why = $"the storefront has no {pageType} template '{row.Template}'";
            return row;
        }

        foreach (var (name, value) in rule["when"] as JsonObject ?? [])
        {
            var holds = TemplateRuleConditions.Evaluate(name, value, pageType, subject, out var notEvaluated);
            row.Conditions.Add(new ConditionEvaluationRow { Name = name, Value = value?.DeepClone(), Holds = holds, NotEvaluated = notEvaluated });
        }

        if (row.Conditions.Any(x => x.Holds == false))
        {
            row.Outcome = RuleOutcome.DoesNotMatch;
        }
        else
        {
            row.Outcome = row.Conditions.Any(x => x.Holds == null) ? RuleOutcome.MayMatch : RuleOutcome.Matches;
        }

        return row;
    }

    private async Task<(TemplateRulesDocument Rules, string Path)> ReadRulesAsync(ClaimsPrincipal user, string storeId, bool draftTheme,
        CancellationToken cancellationToken)
    {
        if (draftTheme)
        {
            var (draft, file) = await ThemeTemplates.ReadRulesAsync(designerOperationService, themeStorageFactory, user, storeId, cancellationToken);
            return (draft, file.Path);
        }

        var path = await ThemeFiles.GetPathAsync(themeStorageFactory, storeId, ThemeStorage.TemplateRulesFile);
        var rules = TemplateRulesDocument.Read(await ThemeFiles.ReadLiveAsync(contentQueryService, user, storeId, path, cancellationToken))
            ?? throw new McpException($"The published template rules of store '{storeId}' ({path}) are not a JSON object, so they cannot be read here.");

        return (rules, path);
    }

    private static void SetLink(PreviewAnswer answer, SiteBuilderOperationResult<SiteBuilderPreviewLink> link, string shows, string notFound = null)
    {
        switch (link.Status)
        {
            case SiteBuilderOperationStatus.Succeeded:
                answer.PreviewUrl = link.Value?.Url;
                answer.Notes.Add(shows);
                break;
            case SiteBuilderOperationStatus.Forbidden:
                throw new McpForbiddenException($"Your builder:read permission does not cover store '{answer.StoreId}'.");
            case SiteBuilderOperationStatus.NotFound:
                answer.Notes.Add($"No preview link: {notFound ?? "the page was not found."}");
                break;
            default:
                answer.Notes.Add($"No preview link: {link.Error}");
                break;
        }
    }
}

public sealed class PreviewAnswer
{
    public string StoreId { get; init; }

    public string Subject { get; init; }

    // Open it in a browser; null when there is none, with the reason in notes.
    public string PreviewUrl { get; set; }

    // template_key: the rules that give the template its pages.
    public IList<string> PickedBy { get; set; }

    // page_id, product_id, category_id: the template the rules pick, as the server calculates it.
    public TemplateChoice Template { get; set; }

    public IList<string> Notes { get; set; } = [];
}

public sealed class TemplateChoice
{
    public string PageType { get; init; }

    public string Template { get; set; }

    // The rule that picks it; null for the type's default template or the page's own.
    public string RuleId { get; set; }

    // false when an earlier rule depends on a condition the server did not evaluate.
    public bool Decided { get; set; }

    public string Reason { get; set; }

    // published or draft: the theme the calculation read.
    public string Theme { get; init; }

    public string RulesFile { get; init; }

    // Every rule tried, in order, up to the one that picks.
    public IList<RuleEvaluationRow> Rules { get; set; } = [];

    // Conditions the calculation could not evaluate, by name.
    public IList<string> NotEvaluated { get; set; } = [];

    public string Note { get; init; }

    public IList<string> Notes { get; set; } = [];
}

public static class RuleOutcome
{
    public const string Matches = "matches";
    public const string DoesNotMatch = "does_not_match";
    public const string MayMatch = "may_match";
    public const string Skipped = "skipped";
}

public sealed class RuleEvaluationRow
{
    public string Id { get; init; }

    public string Template { get; init; }

    // matches, does_not_match, may_match (a condition was not evaluated) or skipped.
    public string Outcome { get; set; }

    public string Why { get; set; }

    public IList<ConditionEvaluationRow> Conditions { get; set; } = [];
}

public sealed class ConditionEvaluationRow
{
    public string Name { get; init; }

    public JsonNode Value { get; init; }

    // null when the server did not evaluate it.
    public bool? Holds { get; init; }

    public string NotEvaluated { get; init; }
}
