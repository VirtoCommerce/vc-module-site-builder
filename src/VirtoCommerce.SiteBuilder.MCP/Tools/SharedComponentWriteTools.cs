using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security.Permissions;

namespace VirtoCommerce.SiteBuilder.MCP.Tools;

// A new Shared Component stands on no page, so creating it changes nothing anyone sees and needs no second person. Its
// content, once it is placed, shows on every page at once, the published ones too: a change of the content waits for a
// second person, who sees the pages it stands on; a new name shows nowhere on the storefront and runs at once. The
// approved change runs the operations again, so it names what they ran on (expected_versions): the component's content
// and the content of every page sections are copied from. The save of a component's content takes no version, so the
// tool reads them again right before it; a save landing between that read and the write is not seen.
[McpServerToolType]
[McpScope(McpScopes.Admin)]
public class SharedComponentWriteTools(
    IAuthorizationService authorizationService,
    IGroupedPageService groupedPageService,
    IGroupedPageContentService pageContentService,
    ISiteBuilderSharedComponentService sharedComponentService,
    ISiteBuilderSharedComponentContentService sharedComponentContentService,
    ISiteBuilderSharedComponentOperationService sharedComponentOperationService)
{
    public const string OperationsArgument = "operations";
    private const int MaxOperations = 100;
    private const string ComponentKeyPrefix = "shared_component:";
    private const string PageKeyPrefix = "page:";

    [McpServerTool(Name = "create_shared_component", ReadOnly = false, Destructive = false, Idempotent = false)]
    [McpPermission(Read, SharedComponentsRead)]
    [Authorize(SharedComponentsCreate)]
    [Description("Create a Shared Component in a store: sections kept once, to place on many pages. It starts on no page, so " +
        "nobody sees it until it is placed with edit_page_draft (place_shared_component). Its content is the sections you " +
        "pass, or sections copied from a page (source_page_id, optionally section_ids), or both: the copied ones first. " +
        "Returns the new component's id and its sections. Call it with dryRun first to show the user the content. To change " +
        "a component later, use revise_shared_component.")]
    public async Task<SharedComponentChange> CreateSharedComponent(
        RequestContext<CallToolRequestParams> context,
        [Description("Store id.")] string store_id,
        [Description("Name of the component, as the designer lists it.")] string name,
        [Description("New sections, in order: each with its type (for example 'hero-banner') and settings.")] NewSection[] sections = null,
        [Description("A page to copy sections from (search_site_pages); you need builder:read in its store.")] string source_page_id = null,
        [Description("With source_page_id: ids of the sections to copy. Omit it to copy every section of the page.")] string[] section_ids = null,
        [Description("true to check the component and return its content, without creating it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(store_id) || string.IsNullOrWhiteSpace(name))
        {
            throw new McpException("Pass store_id and name: a Shared Component belongs to one store.");
        }

        var storeId = store_id.Trim();
        await authorizationService.EnsureAllowedAsync(context.User, new SiteBuilderSharedComponentSearchCriteria { StoreId = storeId }, storeId, SharedComponentsRead);

        var operations = new List<PageEditOperation>();
        if (!string.IsNullOrWhiteSpace(source_page_id))
        {
            operations.Add(new PageEditOperation { Op = "copy_sections_from", SourcePageId = source_page_id.Trim(), SectionIds = section_ids });
        }
        else if (section_ids?.Length > 0)
        {
            throw new McpException("section_ids go with source_page_id, the page to copy them from.");
        }

        operations.AddRange((sections ?? []).Select(x => x?.ToOperation() ?? throw new McpException("A section is empty.")));
        if (operations.Count == 0)
        {
            throw new McpException("Pass sections, source_page_id or both: a Shared Component starts with sections.");
        }

        var editor = PageDocumentEditor.Read(null, PageDocumentKind.SharedComponent);
        await editor.ApplyAsync(operations, CreateSources(context.User, storeId), cancellationToken);
        var content = JObject.Parse(editor.Write());

        var change = new SharedComponentChange
        {
            Action = "create_shared_component",
            ObjectType = ChangePreview.SharedComponent,
            ObjectName = name.Trim(),
            StoreId = storeId,
        };

        SharedComponentCreateOperationRequest Request(bool isDryRun) => new()
        {
            StoreId = storeId,
            Name = name.Trim(),
            Content = content,
            DryRun = isDryRun,
        };
        var subject = $"A Shared Component of store '{storeId}'";

        var check = await sharedComponentOperationService.CreateAsync(Request(true), context.User, cancellationToken);
        EnsureSucceeded(check, subject, storeId, SharedComponentsCreate);
        change.DryRun = true;
        change.AddChanges(check.Preview);
        change.Sections = SectionRow.From(editor.Sections);
        if (dryRun)
        {
            return change;
        }

        var result = await sharedComponentOperationService.CreateAsync(Request(false), context.User, cancellationToken);
        EnsureSucceeded(result, subject, storeId, SharedComponentsCreate);
        change.DryRun = false;
        change.ObjectId = result.Value?.Id;
        change.SharedComponentId = result.Value?.Id;
        change.AddWarning("The component stands on no page yet: place it with edit_page_draft (place_shared_component).");

        return change;
    }

    [McpServerTool(Name = "revise_shared_component", ReadOnly = false, Destructive = true, Idempotent = false)]
    [McpPermission(Read, SharedComponentsRead)]
    [Authorize(SharedComponentsUpdate)]
    [McpRequiresApproval(Condition = nameof(NeedsApproval))]
    [Description("Change a Shared Component: its content with operations (the operations of edit_page_draft, on the component's " +
        "sections), or its name. The content shows at once on every page the component stands on, published ones too, so a " +
        "call with operations waits for a second person's approval, who sees the change and the pages it reaches. A new name " +
        "shows nowhere on the storefront, so a call with name only runs at once. Read the component first with " +
        "search_shared_components (shared_component_id) for its section ids. Call it with dryRun first: it shows the user the " +
        "change and the pages, and answers expected_versions (the version of the component's content and of every page " +
        "copy_sections_from copies from). Then call it with operations and those expected_versions unchanged: it waits for " +
        "approval, and is refused if the component or a copied page changed since. To change one page only, detach the " +
        "component there with edit_page_draft instead.")]
    public async Task<SharedComponentChange> ReviseSharedComponent(
        RequestContext<CallToolRequestParams> context,
        [Description("Id of the component (search_shared_components).")] string shared_component_id,
        [Description("A new name. Omit it to keep the name.")] string name = null,
        [Description("Changes of the content, applied in order: add_section, duplicate_section, move_section, hide_section, " +
            "show_section, remove_section, set_section_settings, add_block, duplicate_block, move_block, hide_block, show_block, " +
            "remove_block, set_page_settings and copy_sections_from, as in edit_page_draft. Passing them makes the call wait for approval.")] PageEditOperation[] operations = null,
        [Description("With operations: the expected_versions of the dry run, unchanged. Required without dryRun, so that the approved " +
            "change runs on exactly the content that was reviewed.")] Dictionary<string, string> expected_versions = null,
        [Description("true to return what would change and the pages the component stands on, without changing it.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shared_component_id))
        {
            throw new McpException("Pass shared_component_id; search_shared_components finds it.");
        }

        if (name == null && operations == null)
        {
            throw new McpException("Pass operations to change the content, name to rename the component, or both.");
        }

        var component = await sharedComponentService.GetByIdAsync(shared_component_id.Trim())
            ?? throw new McpException($"Shared Component '{shared_component_id}' was not found. Call search_shared_components to find its id.");
        await authorizationService.EnsureAllowedAsync(context.User, component, component.StoreId, SharedComponentsRead);

        var change = new SharedComponentChange
        {
            Action = "revise_shared_component",
            ObjectType = ChangePreview.SharedComponent,
            ObjectId = component.Id,
            ObjectName = component.Name,
            SharedComponentId = component.Id,
            StoreId = component.StoreId,
            DryRun = true,
        };
        var subject = $"Shared Component '{component.Id}'";

        // Every check first, the content's and the name's, so that a refused part writes nothing of the other.
        var content = operations == null
            ? null
            : await CheckContentAsync(context.User, component, operations, new ReviewedCall(expected_versions, dryRun), change, cancellationToken);

        var newName = name?.Trim();
        var rename = newName != null && !string.Equals(newName, component.Name, StringComparison.Ordinal);
        if (rename)
        {
            var check = await sharedComponentOperationService.UpdateAsync(RenameRequest(component, newName, isDryRun: true), context.User, cancellationToken);
            EnsureSucceeded(check, subject, component.StoreId, SharedComponentsUpdate);
            change.AddChanges(check.Preview);
        }
        else if (newName != null)
        {
            change.AddWarning($"The component is already named '{component.Name}'.");
        }

        if (content == null && !rename)
        {
            change.AddWarning("The call changes nothing, so nothing was written.");
            return change;
        }

        if (dryRun)
        {
            return change;
        }

        if (content != null)
        {
            ReviewedVersions.EnsureUnchanged(change, await ReadVersionsAsync(component, change.ExpectedVersions.Keys, cancellationToken));
            var result = await sharedComponentOperationService.SaveContentAsync(ContentRequest(component, content, isDryRun: false), context.User, cancellationToken);
            EnsureSucceeded(result, subject, component.StoreId, SharedComponentsUpdate);
            change.AffectsPublished = result.AffectsPublished;
        }

        if (rename)
        {
            var result = await sharedComponentOperationService.UpdateAsync(RenameRequest(component, newName, isDryRun: false), context.User, cancellationToken);
            EnsureSucceeded(result, subject, component.StoreId, SharedComponentsUpdate);
            change.ObjectName = newName;
        }

        change.DryRun = false;
        return change;
    }

    // A call that changes the content waits for a second person once it names the versions it reviewed; without them it
    // is a dry run or is refused by the tool, so no content changes without approval either way. A rename runs at once.
    public static bool NeedsApproval(IReadOnlyDictionary<string, JsonElement> arguments) =>
        arguments != null &&
        arguments.TryGetValue(OperationsArgument, out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
        ReviewedVersions.AreGiven(arguments);

    // The content with the operations applied, null when they change nothing, checked by the operation's dry run of its
    // save (the change and the pages it reaches) and pinned to the versions it was edited from.
    private async Task<JObject> CheckContentAsync(ClaimsPrincipal user, SiteBuilderSharedComponent component, PageEditOperation[] operations, ReviewedCall call,
        SharedComponentChange change, CancellationToken cancellationToken)
    {
        var (content, versions) = await EditContentAsync(user, component, operations, change, cancellationToken);
        if (content == null)
        {
            return null;
        }

        var check = await sharedComponentOperationService.SaveContentAsync(ContentRequest(component, content, isDryRun: true), user, cancellationToken);
        EnsureSucceeded(check, $"Shared Component '{component.Id}'", component.StoreId, SharedComponentsUpdate);
        change.AddChanges(check.Preview);
        change.AffectsPublished = check.AffectsPublished;
        change.PlacedOnPages = check.Preview?.Objects.Where(x => x.ObjectType == ChangePreview.Page).Select(x => x.ObjectId).ToList() ?? [];
        foreach (var warning in check.Preview?.Warnings ?? [])
        {
            change.AddWarning(warning);
        }

        ReviewedVersions.Pin(change, versions, call.ExpectedVersions, call.DryRun);
        return content;
    }

    private sealed record ReviewedCall(IDictionary<string, string> ExpectedVersions, bool DryRun);

    // The versions of what the content was edited from, read again: the component's content and the pages copied from.
    private async Task<IList<KeyValuePair<string, string>>> ReadVersionsAsync(SiteBuilderSharedComponent component, IEnumerable<string> keys,
        CancellationToken cancellationToken)
    {
        var versions = new List<KeyValuePair<string, string>>
        {
            new(ComponentKeyPrefix + component.Id, VersionOf(await sharedComponentContentService.TryLoadContentAsync(component, cancellationToken))),
        };

        foreach (var pageId in keys.Where(x => x.StartsWith(PageKeyPrefix, StringComparison.OrdinalIgnoreCase)).Select(x => x[PageKeyPrefix.Length..]))
        {
            var group = await groupedPageService.GetByIdAsync(pageId);
            var version = group == null ? null : (await PageEditSources.LoadDraftAsync(pageContentService, group, cancellationToken)).ETag;
            versions.Add(new(PageKeyPrefix + pageId, version));
        }

        return versions;
    }

    // The content of a component keeps no version of its own: its version is the hash of the content, as of a page stored as a file.
    private static string VersionOf(string content) => SiteBuilderContentVersion.CreateForFileContent(content);

    // The component's content with the operations applied, null when they change nothing, and the versions of what it
    // was edited from.
    private async Task<(JObject Content, IList<KeyValuePair<string, string>> Versions)> EditContentAsync(ClaimsPrincipal user,
        SiteBuilderSharedComponent component, PageEditOperation[] operations, SharedComponentChange change, CancellationToken cancellationToken)
    {
        if (operations.Length == 0)
        {
            throw new McpException("Pass the operations that change the content, or omit operations to rename only.");
        }

        if (operations.Length > MaxOperations)
        {
            throw new McpException($"Pass at most {MaxOperations} operations in one call.");
        }

        var current = await sharedComponentContentService.TryLoadContentAsync(component, cancellationToken);
        var editor = PageDocumentEditor.Read(current, PageDocumentKind.SharedComponent)
            ?? throw new McpException($"The content of Shared Component '{component.Id}' is not a page document, so it cannot be edited here. Open it in the designer.");
        var before = PageDocumentEditor.Read(current, PageDocumentKind.SharedComponent);

        var sources = CreateSources(user, component.StoreId);
        await editor.ApplyAsync(operations, sources, cancellationToken);
        change.Sections = SectionRow.From(editor.Sections);
        if (PageDocumentChanges.Compare(before, editor).Count == 0)
        {
            change.AddWarning("The operations change nothing in the content.");
            return (null, []);
        }

        var versions = new List<KeyValuePair<string, string>> { new(ComponentKeyPrefix + component.Id, VersionOf(current)) };
        versions.AddRange(sources.PageVersions.Select(x => new KeyValuePair<string, string>(PageKeyPrefix + x.Key, x.Value)));

        return (JObject.Parse(editor.Write()), versions);
    }

    private PageEditSources CreateSources(ClaimsPrincipal user, string storeId) =>
        new(authorizationService, groupedPageService, pageContentService, sharedComponentService, sharedComponentContentService, user, storeId);

    private static SharedComponentContentSaveOperationRequest ContentRequest(SiteBuilderSharedComponent component, JObject content, bool isDryRun) =>
        new() { Id = component.Id, Content = content, DryRun = isDryRun, ReportImpact = true };

    private static SharedComponentUpdateOperationRequest RenameRequest(SiteBuilderSharedComponent component, string name, bool isDryRun) =>
        new() { Id = component.Id, Name = name, DryRun = isDryRun, ReportImpact = true };

    private static void EnsureSucceeded(SiteBuilderOperationResult result, string subject, string storeId, string permission)
    {
        switch (result.Status)
        {
            case SiteBuilderOperationStatus.Succeeded:
                return;
            case SiteBuilderOperationStatus.Forbidden:
                throw new McpForbiddenException($"You may not change {subject}: it needs {permission}, and builder:shared-components:read in store '{storeId}'.");
            case SiteBuilderOperationStatus.NotFound:
                throw new McpException($"{subject} was not found, or was deleted meanwhile. Call search_shared_components to find it.");
            default:
                var details = result.Errors?.Count > 1 ? $" {string.Join(" ", result.Errors)}" : string.Empty;
                throw new McpException($"{subject} was not changed: {result.Error}{details}");
        }
    }
}

// A section of a new Shared Component.
public sealed class NewSection
{
    [JsonPropertyName("type")]
    [Description("Section type, for example 'hero-banner'. Use the types the store's pages already use.")]
    public string Type { get; set; }

    [JsonPropertyName("settings")]
    [Description("Values of the section's settings, as in add_section of edit_page_draft.")]
    public Dictionary<string, JsonNode> Settings { get; set; }

    [JsonPropertyName("language")]
    [Description("For settings that hold a text per language: the language the values are in.")]
    public string Language { get; set; }

    public PageEditOperation ToOperation() => new() { Op = "add_section", Type = Type, Settings = Settings, Language = Language };
}

public sealed class SharedComponentChange : McpChangePreview, IReviewedChange
{
    [JsonPropertyName("shared_component_id")]
    public string SharedComponentId { get; set; }

    [JsonPropertyName("store_id")]
    public string StoreId { get; set; }

    // The content shows on a published page as soon as it is saved.
    [JsonPropertyName("affects_published")]
    public bool AffectsPublished { get; set; }

    // The pages the component stands on, which show a change of its content at once; null when the content is not
    // changed, and empty for pages you may not read (the warnings tell how many).
    [JsonPropertyName("placed_on_pages")]
    public IList<string> PlacedOnPages { get; set; }

    // The component's sections after the change, in order.
    [JsonPropertyName("sections")]
    public IList<SectionRow> Sections { get; set; }

    // A change of the content: the version of the component's content ('shared_component:<id>') and of each page its
    // sections are copied from ('page:<id>'). Sent back unchanged, the write takes exactly these and is refused once one moved.
    [JsonPropertyName("expected_versions")]
    public IDictionary<string, string> ExpectedVersions { get; set; }
}
