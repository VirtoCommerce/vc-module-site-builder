using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
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

// The content author's edits of a database page's draft: what customers see changes only when the draft is published,
// so the edit needs no second person. It saves through the designer's own save, with its checks, sanitizing and history.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class PageDraftTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService pageContentService,
    IDesignerOperationService designerOperationService,
    ISiteBuilderSharedComponentService sharedComponentService,
    ISiteBuilderSharedComponentContentService sharedComponentContentService)
{
    private const int MaxOperations = 100;

    [McpServerTool(Name = "edit_page_draft", ReadOnly = false, Destructive = false, Idempotent = false)]
    [McpPermission(Read)]
    [Authorize(Update)]
    [Description("Edit the draft of a database page: add, duplicate, move, hide, show and remove sections and their " +
        "blocks, change section and block settings (texts, images, links), change page settings such as " +
        "seoInfo.pageTitle, seoInfo.metaDescription and layout, copy sections from another page, place a Shared " +
        "Component or detach one into the page's own sections. Read the page first with search_site_pages (page_id) " +
        "and pass its version_token; the operations apply in order, and all or none are saved. Customers see the " +
        "change only after the draft is published. Returns the changes section by section, the new version_token and " +
        "the page's sections. Call it with dryRun first to show the user the change. Not for the page's name, address, " +
        "language, audience or dates, nor for pages kept as files.")]
    public async Task<PageDraftEdit> EditPageDraft(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of the page (a Landing Page or a language version of a Multilanguage Page), as search_site_pages returns it.")] string page_id,
        [Description("version_token of the page as search_site_pages with page_id returned it. An edit of an older version is refused.")] string version_token,
        [Description("The changes, applied in order.")] PageEditOperation[] operations,
        [Description("true to check the edit and return what it would change, without saving it. Ids of sections a dry run adds are not kept.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(page_id))
        {
            throw new McpException("Pass page_id; search_site_pages finds it.");
        }

        var group = await groupedPageService.GetByIdAsync(page_id)
            ?? throw new McpException($"Page '{page_id}' was not found. Call search_site_pages to find its id.");

        await authorizationService.EnsureAllowedAsync(context.User, group, group.StoreId);

        if (operations == null || operations.Length == 0)
        {
            throw new McpException("Pass operations: the changes to make.");
        }

        if (operations.Length > MaxOperations)
        {
            throw new McpException($"Pass at most {MaxOperations} operations in one call.");
        }

        var current = await LoadDraftAsync(group, cancellationToken);
        if (string.IsNullOrWhiteSpace(version_token) || version_token.Trim() != current.ETag)
        {
            throw StaleVersion(page_id);
        }

        var editor = PageDocumentEditor.Read(current.Content)
            ?? throw new McpException($"The content of page '{page_id}' is not a page document, so it cannot be edited here. Open it in the designer.");
        var before = PageDocumentEditor.Read(current.Content);

        await editor.ApplyAsync(operations, new Sources(this, context.User, group.StoreId), cancellationToken);
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

        return preview;
    }

    private async Task<SiteBuilderContentSnapshot> LoadDraftAsync(GroupedSiteBuilderPage group, CancellationToken cancellationToken)
    {
        try
        {
            return await pageContentService.LoadGroupContentAsync(group, draft: true, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            throw new McpException($"Page '{group.Id}' was deleted while it was read.");
        }
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

    private static McpException StaleVersion(string pageId) =>
        new($"Page '{pageId}' changed since you read it, or version_token is not its version. Read it again with " +
            "search_site_pages (page_id) and make the edit on that content with its version_token.");

    private async Task<JsonArray> GetPageSectionsAsync(ClaimsPrincipal user, string pageId, CancellationToken cancellationToken)
    {
        var group = await groupedPageService.GetByIdAsync(pageId)
            ?? throw new McpException($"Page '{pageId}' to copy from was not found. Call search_site_pages to find its id.");

        await authorizationService.EnsureAllowedAsync(user, group, group.StoreId);

        var snapshot = await LoadDraftAsync(group, cancellationToken);
        return SiteBuilderPageDocument.Read(snapshot.Content)?.Sections ?? [];
    }

    private async Task<SiteBuilderSharedComponent> GetSharedComponentAsync(ClaimsPrincipal user, string storeId, string sharedComponentId)
    {
        var component = await sharedComponentService.GetByIdAsync(sharedComponentId);
        if (component == null || !storeId.EqualsIgnoreCase(component.StoreId))
        {
            throw new McpException($"Shared Component '{sharedComponentId}' was not found in store '{storeId}'. Call search_shared_components to find its id.");
        }

        await authorizationService.EnsureAllowedAsync(user, component, component.StoreId, SharedComponentsRead);
        return component;
    }

    private async Task<JsonArray> GetSharedComponentSectionsAsync(ClaimsPrincipal user, string storeId, string sharedComponentId,
        CancellationToken cancellationToken)
    {
        var component = await GetSharedComponentAsync(user, storeId, sharedComponentId);
        var content = SiteBuilderPageDocument.Read(await sharedComponentContentService.TryLoadContentAsync(component, cancellationToken));

        return content?.Sections ?? throw new McpException($"Shared Component '{sharedComponentId}' has no content to detach.");
    }

    private sealed class Sources(PageDraftTools tools, ClaimsPrincipal user, string storeId) : IPageEditSources
    {
        public Task<JsonArray> GetPageSectionsAsync(string pageId, CancellationToken cancellationToken) =>
            tools.GetPageSectionsAsync(user, pageId, cancellationToken);

        public Task EnsureSharedComponentAsync(string sharedComponentId, CancellationToken cancellationToken) =>
            tools.GetSharedComponentAsync(user, storeId, sharedComponentId);

        public Task<JsonArray> GetSharedComponentSectionsAsync(string sharedComponentId, CancellationToken cancellationToken) =>
            tools.GetSharedComponentSectionsAsync(user, storeId, sharedComponentId, cancellationToken);
    }
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
