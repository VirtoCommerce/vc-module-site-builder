using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Data.Services;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// The content author's edits of a page's draft: what customers see changes only when the draft is published, so the
// edit needs no second person. It saves through the designer's own save, with its checks, sanitizing and history. A page
// stored as a file has no version of its own; its version token is the hash of its content, which the save compares
// with the draft it replaces.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class PageDraftTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService pageContentService,
    IDesignerOperationService designerOperationService,
    ISiteBuilderSharedComponentService sharedComponentService,
    ISiteBuilderSharedComponentContentService sharedComponentContentService,
    ISiteBuilderContentQueryService contentQueryService,
    ILogger<PageDraftTools> logger)
{
    private const int MaxOperations = 100;
    private const string TemplateSetting = "template";

    [McpServerTool(Name = "edit_page_draft", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [Description("Edit the draft of a page: add, duplicate, move, hide, show and remove sections and their " +
        "blocks, change section and block settings (texts, images, links), change page settings such as " +
        "seoInfo.pageTitle, seoInfo.metaDescription, layout and template (the page's own template, which comes before " +
        "every template rule: the key of a page template of the theme or of the storefront, such as 'page' or " +
        "'page.landing'; search_templates lists them, null removes it), copy sections from another page, place a Shared " +
        "Component or detach one into the page's own sections. Read the page first with search_site_pages (page_id) " +
        "and pass its version_token; the operations apply in order, and all or none are saved. Customers see the " +
        "change only after the draft is published. Returns the changes section by section, the new version_token and " +
        "the page's sections. Call it with dryRun first to show the user the change. A page stored as a file is named " +
        "by its path ('pages/about.page') with store_id, and holds no Shared Component. Not for the page's name, address, " +
        "language, audience or dates.")]
    public async Task<PageDraftEdit> EditPageDraft(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of the page (a Landing Page or a language version of a Multilanguage Page), as search_site_pages returns it, " +
            "or the path of a page stored as a file.")] string page_id,
        [Description("version_token of the page as search_site_pages with page_id returned it. An edit of an older version is refused.")] string version_token,
        [Description("The changes, applied in order.")] PageEditOperation[] operations,
        [Description("With a page stored as a file: the store of the page.")] string store_id = null,
        [Description("true to check the edit and return what it would change, without saving it. Ids of sections a dry run adds are not kept.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(page_id))
        {
            throw new McpException("Pass page_id; search_site_pages finds it.");
        }

        if (PageLookup.IsFilePagePath(page_id))
        {
            return await EditFilePageAsync(context, store_id, page_id, version_token, operations, dryRun, cancellationToken);
        }

        var group = await groupedPageService.GetByIdAsync(page_id)
            ?? throw new McpException($"Page '{page_id}' was not found. Call search_site_pages to find its id.");

        await authorizationService.EnsureAllowedAsync(context.User, group, group.StoreId);
        EnsureOperations(operations);

        var current = await PageEditSources.LoadDraftAsync(pageContentService, group, cancellationToken);
        if (string.IsNullOrWhiteSpace(version_token) || version_token.Trim() != current.ETag)
        {
            throw StaleVersion(page_id);
        }

        var editor = PageDocumentEditor.Read(current.Content)
            ?? throw new McpException($"The content of page '{page_id}' is not a page document, so it cannot be edited here. Open it in the designer.");
        var before = PageDocumentEditor.Read(current.Content);
        var templateWarnings = await CheckTemplatesAsync(operations, context.User, group.StoreId, cancellationToken);

        await editor.ApplyAsync(operations, CreateSources(context, group.StoreId), cancellationToken);
        var content = editor.Write();

        var preview = new PageDraftEdit
        {
            Action = "edit_page_draft",
            ObjectType = "page",
            ObjectId = group.Id,
            ObjectName = group.Name,
            DryRun = dryRun,
            VersionToken = current.ETag,
        };

        if (PageDocumentChanges.Compare(before, editor).Count == 0)
        {
            preview.Sections = SectionRow.From(editor.Sections);
            preview.AddWarning("The operations change nothing, so nothing was saved.");
            return preview;
        }

        var result = await designerOperationService.SaveAsync(new DesignerSaveOperationRequest
        {
            StoreId = group.StoreId,
            Changes = new DesignerSaveRequest
            {
                Page = new DesignerSavePage { GroupId = group.Id, ETag = current.ETag, Content = content },
            },
            SaveId = Guid.NewGuid().ToString("N"),
            DryRun = dryRun,
        }, context.User, cancellationToken);

        EnsureSaved(result, group);

        // The dry run answers the content as it would be stored, sanitized; a save stores what was sent, sanitized the same way.
        var saved = dryRun ? ReadPreviewContent(result.Preview, page_id, current.ETag) : editor;
        foreach (var change in PageDocumentChanges.Compare(before, saved))
        {
            preview.AddChange(change.Field, change.From, change.To);
        }

        preview.VersionToken = dryRun ? current.ETag : result.Value?.ETag;
        preview.Sections = SectionRow.From(saved.Sections);
        if (group.Status == PageStatuses.Published)
        {
            preview.AddWarning("The page is published: customers keep seeing the published version until this draft is published.");
        }

        foreach (var warning in templateWarnings)
        {
            preview.AddWarning(warning);
        }

        return preview;
    }

    // The token is checked against the content read for the edit and against what the save's own dry run reads as the
    // current draft, and the save takes it as the version of the draft it replaces: in git the commit names the file it
    // was compared with, in blob storage the draft is read again right before the write, so a save by someone else in
    // between is refused rather than overwritten (in blob storage, but for the moment between that read and the write).
    private async Task<PageDraftEdit> EditFilePageAsync(RequestContext<CallToolRequestParams> context, string storeId, string pageId, string versionToken,
        PageEditOperation[] operations, bool dryRun, CancellationToken cancellationToken)
    {
        var page = await FilePages.LoadAsync(authorizationService, context.User, storeId, pageId);
        EnsureOperations(operations);

        var current = await FilePages.RequireAsync(contentQueryService, logger, context.User, page, cancellationToken);
        var version = FilePages.VersionOf(current);
        if (string.IsNullOrWhiteSpace(versionToken) || versionToken.Trim() != version)
        {
            throw StaleVersion(page.PageId, page.StoreId);
        }

        var editor = PageDocumentEditor.Read(current, PageDocumentKind.FilePage)
            ?? throw new McpException($"The content of page '{page.PageId}' is not a page document, so it cannot be edited here. Open it in the designer.");
        var before = PageDocumentEditor.Read(current, PageDocumentKind.FilePage);
        var templateWarnings = await CheckTemplatesAsync(operations, context.User, page.StoreId, cancellationToken);

        await editor.ApplyAsync(operations, CreateSources(context, page.StoreId), cancellationToken);

        var preview = new PageDraftEdit
        {
            Action = "edit_page_draft",
            ObjectType = ChangePreview.FilePage,
            ObjectId = page.PageId,
            ObjectName = page.PageId,
            DryRun = dryRun,
            VersionToken = version,
        };

        if (PageDocumentChanges.Compare(before, editor).Count == 0)
        {
            preview.Sections = SectionRow.From(editor.Sections);
            preview.AddWarning("The operations change nothing, so nothing was saved.");
            return preview;
        }

        Task<SiteBuilderOperationResult<DesignerSaveResult>> SaveAsync(bool isDryRun) => FilePages.RunAsync(logger, () => designerOperationService.SaveAsync(
            new DesignerSaveOperationRequest
            {
                StoreId = page.StoreId,
                Changes = new DesignerSaveRequest
                {
                    Page = new DesignerSavePage { Path = page.Path, Type = page.ContentType, Content = editor.Write() },
                },
                SaveId = Guid.NewGuid().ToString("N"),
                DryRun = isDryRun,
                ExpectedFilePageVersion = isDryRun ? null : version,
            }, context.User, cancellationToken));

        var check = await SaveAsync(true);
        EnsureFilePageSaved(check, page);
        var item = check.Preview?.Objects.FirstOrDefault(x => x.ObjectType == ChangePreview.FilePage);
        var saving = item?.Fields.FirstOrDefault(x => x.Name == ChangePreview.Content);
        if ((item?.ETag ?? FilePages.VersionOf(saving?.From)) != version)
        {
            throw StaleVersion(page.PageId, page.StoreId);
        }

        var saved = PageDocumentEditor.Read(saving?.To, PageDocumentKind.FilePage)
            ?? throw new McpException($"The edit of page '{page.PageId}' could not be previewed.");
        foreach (var change in PageDocumentChanges.Compare(before, saved))
        {
            preview.AddChange(change.Field, change.From, change.To);
        }

        preview.Sections = SectionRow.From(saved.Sections);
        foreach (var warning in templateWarnings)
        {
            preview.AddWarning(warning);
        }

        if (dryRun)
        {
            return preview;
        }

        EnsureFilePageSaved(await SaveAsync(false), page);
        preview.VersionToken = FilePages.VersionOf(await FilePages.ReadAsync(contentQueryService, logger, context.User, page, draft: true, cancellationToken));

        return preview;
    }

    // The page's own template is a page template the theme has, as a draft or published, or one of the storefront's own:
    // the storefront ignores a key it cannot find, which would leave the page on the rules without anyone noticing.
    private async Task<IList<string>> CheckTemplatesAsync(PageEditOperation[] operations, ClaimsPrincipal user, string storeId,
        CancellationToken cancellationToken)
    {
        var values = operations
            .Where(x => string.Equals(x.Op?.Trim(), "set_page_settings", StringComparison.OrdinalIgnoreCase) && x.Settings?.ContainsKey(TemplateSetting) == true)
            .Select(x => x.Settings[TemplateSetting])
            .Where(x => x != null)
            .ToList();
        if (values.Count == 0)
        {
            return [];
        }

        var templates = (await ThemeTemplates.ListAsync(contentQueryService, user, storeId, cancellationToken))
            .Where(x => TemplateRulesDocument.PageTypeOf(x.Key) == "page")
            .ToList();
        var known = templates.Select(x => x.Key).Concat(ThemeTemplates.BuiltIn.Where(x => TemplateRulesDocument.PageTypeOf(x) == "page"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var warnings = new List<string>();
        foreach (var value in values)
        {
            if (value is not JsonValue text || !text.TryGetValue<string>(out var key) || string.IsNullOrWhiteSpace(key))
            {
                throw new McpException("The page setting 'template' is the key of a page template, such as 'page' or 'page.landing', or null to remove it. Nothing was changed.");
            }

            if (!known.Contains(key, StringComparer.Ordinal))
            {
                throw new McpException($"There is no page template '{key}' in the theme of store '{storeId}' or among the storefront's own, so the " +
                    $"storefront would ignore it. The page templates: {string.Join(", ", known)}. Pick one of them, or create the template with " +
                    "edit_template first. Nothing was changed.");
            }

            if (templates.FirstOrDefault(x => x.Key == key) is { Published: false })
            {
                warnings.Add($"Template '{key}' is only in the theme's draft: the storefront uses it once the theme is published (publish_theme).");
            }
        }

        return warnings;
    }

    private PageEditSources CreateSources(RequestContext<CallToolRequestParams> context, string storeId) =>
        new(authorizationService, groupedPageService, pageContentService, sharedComponentService, sharedComponentContentService, context.User, storeId);

    private static void EnsureOperations(PageEditOperation[] operations)
    {
        if (operations == null || operations.Length == 0)
        {
            throw new McpException("Pass operations: the changes to make.");
        }

        if (operations.Length > MaxOperations)
        {
            throw new McpException($"Pass at most {MaxOperations} operations in one call.");
        }

        if (Array.IndexOf(operations, null) is var empty and >= 0)
        {
            throw new McpException($"Operation {empty + 1} is empty.");
        }
    }

    private static void EnsureFilePageSaved(SiteBuilderOperationResult result, FilePage page)
    {
        if (result.Status is SiteBuilderOperationStatus.Conflict or SiteBuilderOperationStatus.VersionRequired)
        {
            throw StaleVersion(page.PageId, page.StoreId);
        }

        FilePages.EnsureSucceeded(result, page, "edit");
    }

    // A dry run does not refuse a stale version (the save checks it under its lock); it answers the current one instead.
    private static PageDocumentEditor ReadPreviewContent(SiteBuilderChangePreview preview, string pageId, string versionToken)
    {
        var page = preview?.Objects.FirstOrDefault(x => x.ObjectType == ChangePreview.Page);
        if (page != null && page.ETag != versionToken)
        {
            throw StaleVersion(pageId);
        }

        var content = page?.Fields.FirstOrDefault(x => x.Name == ChangePreview.Content)?.To;

        return PageDocumentEditor.Read(content) ?? throw new McpException($"The edit of page '{pageId}' could not be previewed.");
    }

    private static void EnsureSaved(SiteBuilderOperationResult result, GroupedSiteBuilderPage group)
    {
        switch (result.Status)
        {
            case SiteBuilderOperationStatus.Succeeded:
                return;
            case SiteBuilderOperationStatus.Forbidden:
                throw new McpForbiddenException($"You may not edit page '{group.Id}': it needs builder:read in store '{group.StoreId}', " +
                    "and builder:shared-components:read there when the page places Shared Components.");
            case SiteBuilderOperationStatus.NotFound:
                throw new McpException($"Page '{group.Id}' was deleted while it was edited.");
            case SiteBuilderOperationStatus.Conflict:
            case SiteBuilderOperationStatus.VersionRequired:
                throw StaleVersion(group.Id);
            default:
                var details = result.Errors?.Count > 1 ? $" {string.Join(" ", result.Errors)}" : string.Empty;
                throw new McpException($"Page '{group.Id}' was not saved: {result.Error}{details}");
        }
    }

    private static McpException StaleVersion(string pageId, string storeId = null) =>
        new($"Page '{pageId}' changed since you read it, or version_token is not its version. Read it again with " +
            $"search_site_pages (page_id{(storeId == null ? string.Empty : $" and store_id '{storeId}'")}) and make the edit on that content with its version_token.");
}

public sealed class PageDraftEdit : McpChangePreview
{
    // The version to pass to the next edit: the saved one, or the current one after a dry run.
    [JsonPropertyName("version_token")]
    public string VersionToken { get; set; }

    // The page's sections after the edit, in order.
    [JsonPropertyName("sections")]
    public IList<SectionRow> Sections { get; set; } = [];
}

public sealed class SectionRow
{
    [JsonPropertyName("id")]
    public string Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; }

    [JsonPropertyName("hidden")]
    public bool Hidden { get; init; }

    [JsonPropertyName("shared_component_id")]
    public string SharedComponentId { get; init; }

    [JsonPropertyName("block_count")]
    public int BlockCount { get; init; }

    public static IList<SectionRow> From(JsonArray sections) =>
        sections.OfType<JsonObject>()
            .Select(x => new SectionRow
            {
                Id = SiteBuilderPageItems.GetId(x),
                Type = SiteBuilderPageItems.GetItemType(x),
                Hidden = SiteBuilderPageItems.IsHidden(x),
                SharedComponentId = SiteBuilderPageItems.IsPlacement(x) ? SiteBuilderPageItems.GetComponentId(x) : null,
                BlockCount = SiteBuilderPageItems.GetBlocks(x)?.Count ?? 0,
            })
            .ToList();
}
