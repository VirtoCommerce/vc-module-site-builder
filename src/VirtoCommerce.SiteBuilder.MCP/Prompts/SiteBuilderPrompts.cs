using System.ComponentModel;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Prompts;

// The routines of the content author, the site designer and the person who reviews their changes, offered by MCP
// clients as slash commands. They only say what to do; the tools decide what the user may see and change. A prompt has
// no check of its own, so [McpPermission] hides them from a user who cannot read Site Builder.
[McpServerPromptType]
[McpScope(McpScopes.Admin)]
[McpPermission(Read)]
public static class SiteBuilderPrompts
{
    private const string ReviewRules = """
        Rules:
        - Base every statement on a tool result and quote the evidence: a page id, a count, a finding id, who changed it and when.
        - Change nothing. Ask me which actions to take; publications, address and audience changes of published pages, and theme
          publications wait for a second person's approval.
        """;

    private const string ChangeRules = """
        Rules:
        - Call every changing tool with dryRun first and show me its preview; make the change only when I agree.
        - Never publish without asking me: publish_page and publish_theme wait for a second person's approval. Call them with dryRun
          first, show me what they would publish, then call them with the expected_versions (and draft_owner) of that dry run unchanged.
        - Never delete pages: archive them in the designer if I ask.
        """;

    [McpServerPrompt(Name = "weekly_content_review")]
    [Description("Weekly look at the site's content: what waits for publication, what changed, what is missing, with a short action list.")]
    public static string WeeklyContentReview(
        [Description("Store id. Leave empty for every store you work in.")] string store_id = null)
    {
        return $"""
            Run my weekly content review{StoreClause(store_id)}.

            1. Call get_site_overview{ArgumentClause(store_id)}. Without a store, call it again with each store_id it lists for the full form.
            2. Call search_site_pages{ArgumentClause(store_id)} with modified_since set to seven days ago.
            3. Call find_content_gaps{ArgumentClause(store_id)}; continue with next_skip until is_complete.
            4. For the three pages waiting for publication that changed last, call get_publish_readiness with their page_id.

            Then write the review for me:
            - Three numbers first: pages waiting for publication, pages changed this week, critical and warning findings.
            - Waiting for publication: each page with who changed it, when, and whether it is ready (blockers named).
            - Gaps: missing and outdated translations per language, pages without SEO title or description; at most ten, published pages first.
            - Not checked: what find_content_gaps could not check, and why.
            - Actions: at most five, most urgent first, each with the tool you would use.

            {ReviewRules}
            """;
    }

    [McpServerPrompt(Name = "launch_landing_page")]
    [Description("Create a landing page from a brief, preview it and prepare it for publication.")]
    public static string LaunchLandingPage(
        [Description("What the page is for, for example 'spring sale, like last year's'.")] string goal = null,
        [Description("Store id.")] string store_id = null,
        [Description("Language code of the page, for example en-US. Leave empty for the store's default language.")] string language = null)
    {
        return $"""
            Help me launch a landing page{(string.IsNullOrEmpty(goal) ? "" : $" for \"{goal}\"")}{StoreClause(store_id)}{(string.IsNullOrEmpty(language) ? "" : $" in {language}")}.
            If you do not know the goal or the store yet, ask me first.

            1. Call get_site_overview{ArgumentClause(store_id)} and read site-builder://stores/{StoreOrPlaceholder(store_id)}/schemas for the sections the storefront offers.
            2. Call search_site_pages with a keyword from the goal to find a similar page to start from; read the best one with page_id.
            3. Propose the page: name, address, sections in order with their texts and images, SEO title and description, audience and dates.
            4. When I agree, call create_page with dryRun, then without it (copy_of_page_id when we start from a similar page).
            5. Call get_preview for the new page and give me the link.
            6. Call get_publish_readiness for it and tell me what would block the publication.

            Do not publish: when I ask, call publish_page with dryRun, show me the preview, then call it with its expected_versions; it waits for a second person's approval.

            {ChangeRules}
            """;
    }

    [McpServerPrompt(Name = "translate_site")]
    [Description("Bring the site to a language: find the missing and outdated translations and translate them.")]
    public static string TranslateSite(
        [Description("Language code to translate into, for example fr-FR.")] string language = null,
        [Description("Store id. Leave empty for every store you work in.")] string store_id = null)
    {
        return $"""
            Help me translate the site{StoreClause(store_id)}{(string.IsNullOrEmpty(language) ? "" : $" into {language}")}. If you do not know the language, ask me first.

            1. Call get_site_overview{ArgumentClause(store_id)} and check the language coverage of that language.
            2. Call find_content_gaps with category translations{ArgumentClause(store_id)} and the language; continue with next_skip until is_complete.
            3. Show me the plan: pages to create in the language, translations to update, and how many texts that is.
            4. When I agree: create each missing version with create_page (kind language_version, dryRun first), then read the
               source version with search_site_pages (page_id) and translate its texts and SEO fields with set_translations,
               bound to source_revision_date. Mark a translation up to date only when its source did not change since.
            5. Call find_content_gaps again and report what is left.

            Do not publish the translations: when I ask, call publish_page with dryRun, show me the preview, then call it with its
            expected_versions; it waits for a second person's approval.

            {ChangeRules}
            """;
    }

    [McpServerPrompt(Name = "review_template_rules")]
    [Description("Review the theme's template rules: which template each kind of page gets, rules that never fire, and rules that point nowhere.")]
    public static string ReviewTemplateRules(
        [Description("Store id.")] string store_id = null,
        [Description("Only rules of this page type: product, category or page.")] string page_type = null)
    {
        return $"""
            Review the template rules{StoreClause(store_id)}{(string.IsNullOrEmpty(page_type) ? "" : $" for {page_type} pages")}. If you do not know the store, ask me first.

            1. Call get_site_overview{ArgumentClause(store_id)} for the theme's state.
            2. Call search_templates{ArgumentClause(store_id)}{(string.IsNullOrEmpty(page_type) ? "" : $" with page_type {page_type}")} for the templates and every rule in order.
            3. Read site-builder://template-rule-conditions for what each condition checks.
            4. For two or three typical pages, call get_preview to see which template and which rule they get.

            Then report:
            - The rules in order, each in one line: page type, conditions, template, enabled or not.
            - Problems: a rule after a rule without conditions (it never fires), a disabled rule, a template no rule gives pages,
              a rule on a template that exists only as a draft, and conditions only the storefront can evaluate.
            - What you would change, with edit_template_rules.

            {ReviewRules}
            """;
    }

    [McpServerPrompt(Name = "rebrand_site")]
    [Description("Rebrand the site from a brand color: palette, logos, header and footer, ready for the theme's publication.")]
    public static string RebrandSite(
        [Description("Store id.")] string store_id = null,
        [Description("The main brand color, #rrggbb.")] string primary_color = null,
        [Description("A second brand color, #rrggbb.")] string secondary_color = null)
    {
        return $"""
            Help me rebrand the site{StoreClause(store_id)}{(string.IsNullOrEmpty(primary_color) ? "" : $" from the color {primary_color}")}{(string.IsNullOrEmpty(secondary_color) ? "" : $" and {secondary_color}")}.
            If you do not know the store or the brand color, ask me first.

            1. Call get_site_overview{ArgumentClause(store_id)} for the theme's parts and drafts.
            2. Call edit_theme_draft with part palette, the brand colors and dryRun; show me the generated colors and every
               contrast failure. Save it when I agree.
            3. Ask me for the logos; set them with edit_theme_draft (part brand), dryRun first.
            4. Read the header and footer with edit_theme_draft (part header, then footer, dryRun only) and propose changes.
            5. Call get_preview for a product page and a content page, and give me the links.
            6. Call get_publish_readiness with theme true and tell me what the publication would take and what blocks it.

            Do not publish the theme: when I ask, call publish_theme, which waits for a second person's approval.

            {ChangeRules}
            """;
    }

    [McpServerPrompt(Name = "review_theme_release")]
    [Description("Before the theme is published: every draft it would take, who changed it, and what blocks it.")]
    public static string ReviewThemeRelease(
        [Description("Store id.")] string store_id = null)
    {
        return $"""
            Review the theme's next publication{StoreClause(store_id)}. If you do not know the store, ask me first.

            1. Call get_publish_readiness with theme true{ArgumentClause(store_id)}.
            2. Call get_site_overview{ArgumentClause(store_id)} for the templates, rules and parts in draft.
            3. For each template draft, call search_templates with its template_key for what it holds now.
            4. Call list_versions{ArgumentClause(store_id)} with the theme files of the drafts, when the store keeps history.

            Then report:
            - Go or no-go first, with the blockers (contrast failures, the schema catalog, refused files).
            - Each draft in one line: the file, who changed it last, when, and what it changes.
            - Drafts by other people than me, so I can check with them.
            - Who is affected: every page of the store.

            {ReviewRules}
            """;
    }

    [McpServerPrompt(Name = "review_site_change")]
    [Description("For the person who approves: what a requested publication or change would do, who asked, and what to check before approving.")]
    public static string ReviewSiteChange(
        [Description("Id of the page the change is about.")] string page_id = null,
        [Description("Store id, for the theme or a page stored as a file.")] string store_id = null,
        [Description("Any text, such as 'yes', when the change is the theme's publication.")] string theme = null)
    {
        var target = "the change";
        if (!string.IsNullOrEmpty(page_id))
        {
            target = $"the change of page {page_id}";
        }
        else if (!string.IsNullOrEmpty(theme))
        {
            target = "the theme's publication";
        }

        return $"""
            I approve changes of the site. Help me review {target}{StoreClause(store_id)}. If you do not know what to review, ask me for the page or the store.

            1. Call get_publish_readiness for it{(string.IsNullOrEmpty(page_id) ? "" : $" with page_id {page_id}")}{ArgumentClause(store_id)}{(string.IsNullOrEmpty(theme) ? "" : " and theme true")}.
            2. For a page, call search_site_pages with its page_id for its settings and content, and get_preview for the link.
            3. Call list_versions{ArgumentClause(store_id)} for who saved what, when the store keeps history.

            Then report:
            - Approve or not first, with the reason in one sentence.
            - What customers will see change, part by part, and who made each change.
            - Who sees it: store, address, language, audience and dates.
            - Risks: critical findings first, then warnings.

            {ReviewRules}
            """;
    }

    private static string StoreClause(string storeId)
    {
        return string.IsNullOrEmpty(storeId) ? string.Empty : $" in store {storeId}";
    }

    private static string StoreOrPlaceholder(string storeId)
    {
        return string.IsNullOrEmpty(storeId) ? "<store_id>" : storeId;
    }

    private static string ArgumentClause(string storeId)
    {
        return string.IsNullOrEmpty(storeId) ? string.Empty : $" with store_id {storeId}";
    }
}
