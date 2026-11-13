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
using VirtoCommerce.SiteBuilder.Data.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// New pages, always as drafts: nothing a customer sees changes until a page is published, so creating one needs no
// second person. Each kind goes through the operation of its endpoint, with its checks and its dry run.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class PageCreateTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService pageContentService,
    IMultilanguagePageService multilanguagePageService,
    ISiteBuilderPageListService pageListService,
    ISiteBuilderSharedComponentService sharedComponentService,
    ISiteBuilderSharedComponentContentService sharedComponentContentService,
    IGroupedPageOperationService groupedPageOperationService,
    IMultilanguagePageOperationService multilanguagePageOperationService)
{
    public const string LandingPage = "landing_page";
    public const string LanguageVersion = "language_version";
    public const string MultilanguagePageCopy = "multilanguage_page_copy";

    private const int MaxOperations = 100;

    [McpServerTool(Name = "create_page", ReadOnly = false, Destructive = false, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Create)]
    [Description("Create a database page as a draft; customers see it only after it is published (publish_page). kind " +
        "'landing_page' (default) creates a Landing Page in store_id: empty, or a copy of another page (copy_of_page_id: " +
        "a page used as a template, with its sections, audience and dates), and with sections added or changed right " +
        "away by the operations of edit_page_draft. kind 'language_version' adds the missing language of a " +
        "Multilanguage Page, starting from its source language's content. kind 'multilanguage_page_copy' copies a " +
        "Multilanguage Page with every language version. Returns the new page's id, its settings and sections; read it " +
        "with search_site_pages (page_id) for its version_token before editing it with edit_page_draft. Call it with " +
        "dryRun first to show the user what will be created.")]
    public async Task<SiteBuilderChange> CreatePage(
        RequestContext<CallToolRequestParams> context,
        [Description("'landing_page' (default), 'language_version' or 'multilanguage_page_copy'.")] string kind = null,
        [Description("landing_page: the store of the new page. Defaults to the store of copy_of_page_id.")] string store_id = null,
        [Description("landing_page: the page's name. Required for a new page; a copy defaults to the source's name with a copy suffix.")] string name = null,
        [Description("landing_page: the page's address, for example '/spring-sale'. A copy defaults to the source's address with a copy suffix.")] string permalink = null,
        [Description("landing_page: language code of the page, for example en-US. language_version: the language to add, one of the store's languages.")] string language = null,
        [Description("landing_page: id of a page to copy (search_site_pages); its draft, or its published version when it has no draft, is copied.")] string copy_of_page_id = null,
        [Description("language_version and multilanguage_page_copy: id of the Multilanguage Page (the id of its row in search_site_pages).")] string multilanguage_page_id = null,
        [Description("language_version: false to start the new language empty instead of from the source language's content.")] bool copy_content = true,
        [Description("landing_page: changes to the page's sections and page settings, as the operations of edit_page_draft, applied in order to the new (or copied) page.")] PageEditOperation[] sections = null,
        [Description("true to check the page and return what would be created, without creating it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        return (kind ?? LandingPage) switch
        {
            LandingPage => await CreateLandingPageAsync(context.User, store_id, name, permalink, language, copy_of_page_id, sections, dryRun, cancellationToken),
            LanguageVersion => await CreateLanguageVersionAsync(context.User, multilanguage_page_id, language, copy_content, sections, dryRun, cancellationToken),
            MultilanguagePageCopy => await CopyMultilanguagePageAsync(context.User, multilanguage_page_id, sections, dryRun, cancellationToken),
            _ => throw new McpException($"kind must be '{LandingPage}', '{LanguageVersion}' or '{MultilanguagePageCopy}'."),
        };
    }

    private async Task<SiteBuilderChange> CreateLandingPageAsync(ClaimsPrincipal user, string storeId, string name, string permalink, string language,
        string copyOfPageId, PageEditOperation[] sections, bool dryRun, CancellationToken cancellationToken)
    {
        if (sections?.Length > MaxOperations)
        {
            throw new McpException($"Pass at most {MaxOperations} operations in sections.");
        }

        var page = new CreateGroupedPageRequest { StoreId = storeId, Name = name, Permalink = permalink, CultureName = language, Visibility = true };
        var content = DefaultPageContent;

        if (!string.IsNullOrEmpty(copyOfPageId))
        {
            var source = await groupedPageService.LoadPageAsync(authorizationService, user, copyOfPageId, "copy_of_page_id");
            var copyName = string.IsNullOrEmpty(name) || string.IsNullOrEmpty(permalink) ? await pageListService.GetCopyNameAsync(source, cancellationToken) : null;

            page.StoreId ??= source.StoreId;
            page.Name ??= copyName?.Name;
            page.Permalink ??= copyName?.Permalink;
            page.CultureName ??= source.CultureName;
            page.Visibility = source.Visibility;
            page.UserGroups = source.UserGroups;
            page.OrganizationId = source.OrganizationId;
            page.StartDate = source.StartDate;
            page.EndDate = source.EndDate;
            content = (await PageEditSources.LoadDraftAsync(pageContentService, source, cancellationToken)).Content ?? DefaultPageContent;
        }

        if (string.IsNullOrWhiteSpace(page.StoreId))
        {
            throw new McpException("Pass store_id: the store of the new page.");
        }

        await authorizationService.EnsureAllowedAsync(user, page, page.StoreId);

        if (string.IsNullOrWhiteSpace(page.Name))
        {
            throw new McpException("Pass name: the name of the new page.");
        }

        var editor = PageDocumentEditor.Read(content);
        if (editor == null || editor.IsLegacyShape)
        {
            throw new McpException($"Page '{copyOfPageId}' is stored in an older format and cannot be copied here. Open it in the designer and save it once, then copy it.");
        }

        if (sections?.Length > 0)
        {
            var sources = new PageEditSources(authorizationService, groupedPageService, pageContentService, sharedComponentService,
                sharedComponentContentService, user, page.StoreId);
            await editor.ApplyAsync(sections, sources, cancellationToken);
        }

        // The name, address and language the content carries are the page's own, as a new page gets them.
        page.Content = SiteBuilderContentSettings.Synchronize(editor.Write(),
            new GroupedSiteBuilderPage { Name = page.Name, Permalink = page.Permalink, CultureName = page.CultureName });

        var change = new SiteBuilderChange { Action = "create_page", ObjectType = ChangePreview.Page, ObjectName = page.Name, DryRun = dryRun };
        var (_, result) = await SiteBuilderChanges.RunAsync(change,
            isDryRun => groupedPageOperationService.CreateWithContentAsync(new GroupedPageCreateOperationRequest { Page = page, DryRun = isDryRun }, user, cancellationToken),
            x => SiteBuilderChanges.EnsureSucceeded(x, "The page",
                $"You may not create pages in store '{page.StoreId}', or not with these Shared Components: it needs builder:read there, " +
                "and builder:shared-components:read for a page that places Shared Components."),
            dryRun);

        change.ObjectId = change.PageId = result.Value?.Id;
        change.Sections = SectionRow.From(PageDocumentEditor.Read(page.Content).Sections);

        return change;
    }

    private async Task<SiteBuilderChange> CreateLanguageVersionAsync(ClaimsPrincipal user, string multilanguagePageId, string language, bool copyContent,
        PageEditOperation[] sections, bool dryRun, CancellationToken cancellationToken)
    {
        RefuseSections(sections, LanguageVersion);

        var root = await multilanguagePageService.LoadMultilanguagePageAsync(authorizationService, user, multilanguagePageId);
        if (string.IsNullOrWhiteSpace(language))
        {
            throw new McpException("Pass language: the store language to add, for example fr-FR.");
        }

        var change = new SiteBuilderChange
        {
            Action = "create_page",
            ObjectType = ChangePreview.Page,
            ObjectName = $"{root.Name} ({language})",
            DryRun = dryRun,
            MultilanguagePageId = root.Id,
        };
        var (_, result) = await SiteBuilderChanges.RunAsync(change,
            isDryRun => multilanguagePageOperationService.CreateLanguageAsync(new MultilanguagePageLanguageOperationRequest
            {
                MultilanguagePageId = root.Id,
                CultureName = language,
                CopyContent = copyContent,
                DryRun = isDryRun,
                ReportImpact = true,
            }, user, cancellationToken),
            x => SiteBuilderChanges.EnsureSucceeded(x, $"Multilanguage Page '{root.Id}'", PageLookup.ForbiddenMultilanguagePage(root)),
            dryRun);

        change.ObjectId = change.PageId = result.Value?.Id;

        return change;
    }

    private async Task<SiteBuilderChange> CopyMultilanguagePageAsync(ClaimsPrincipal user, string multilanguagePageId, PageEditOperation[] sections, bool dryRun,
        CancellationToken cancellationToken)
    {
        RefuseSections(sections, MultilanguagePageCopy);

        var root = await multilanguagePageService.LoadMultilanguagePageAsync(authorizationService, user, multilanguagePageId);

        var change = new SiteBuilderChange
        {
            Action = "create_page",
            ObjectType = ChangePreview.MultilanguagePage,
            ObjectName = root.Name,
            DryRun = dryRun,
        };
        var (_, result) = await SiteBuilderChanges.RunAsync(change,
            isDryRun => multilanguagePageOperationService.CloneAsync(new MultilanguagePageOperationRequest { MultilanguagePageId = root.Id, DryRun = isDryRun, ReportImpact = true },
                user, cancellationToken),
            x => SiteBuilderChanges.EnsureSucceeded(x, $"Multilanguage Page '{root.Id}'", PageLookup.ForbiddenMultilanguagePage(root)),
            dryRun);

        change.ObjectId = change.MultilanguagePageId = result.Value?.Id;

        return change;
    }

    private static void RefuseSections(PageEditOperation[] sections, string kind)
    {
        if (sections?.Length > 0)
        {
            throw new McpException($"sections are taken with kind '{LandingPage}' only. Create the {kind.Replace('_', ' ')} first, then edit it with edit_page_draft.");
        }
    }
}
