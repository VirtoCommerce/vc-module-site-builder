using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Core.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.MCP.Tools;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;
using static VirtoCommerce.SiteBuilder.Data.Services.SiteBuilderPageItems;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

public sealed class ThemeFileChange : McpChangePreview
{
    [JsonPropertyName("store_id")]
    public string StoreId { get; set; }

    [JsonPropertyName("version_tokens")]
    public IDictionary<string, string> VersionTokens { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("sections")]
    public IList<SectionRow> Sections { get; set; }

    [JsonPropertyName("rules")]
    public IList<TemplateRuleRow> Rules { get; set; }

    [JsonPropertyName("values")]
    public JsonObject Values { get; set; }

    [JsonPropertyName("contrast_failures")]
    public IList<ContrastFailureRow> ContrastFailures { get; set; }
}

public sealed record ThemeFileEdit(string Path, ThemeFileContent Current, string Content);

public sealed record ThemeFileSaveTarget(string StoreId, string CatalogHash, bool DryRun, string Permission, string ReadAgain);

public static class ThemeFileEdits
{
    // The diff is of the content as stored: on a dry run, the sanitized content the save would write.
    public static async Task SaveAsync(ThemeFileChange change, IDesignerOperationService designerOperationService, ClaimsPrincipal user,
        ThemeFileSaveTarget target, IList<ThemeFileEdit> edits, CancellationToken cancellationToken)
    {
        change.DryRun = target.DryRun;
        foreach (var edit in edits)
        {
            change.VersionTokens[edit.Path] = edit.Current.ETag;
        }

        var changed = edits.Where(x => Compare(x.Path, x.Current.Content, x.Content).Count > 0).ToList();
        if (changed.Count == 0)
        {
            change.AddWarning("The call changes nothing, so nothing was saved.");
            return;
        }

        var files = changed.Select(x => new DesignerSaveFile { Path = x.Path, Content = x.Content, ETag = x.Current.ETag }).ToList();
        var result = await ThemeFiles.SaveAsync(designerOperationService, user, target.StoreId, target.CatalogHash, files, target.DryRun, cancellationToken);
        ThemeFiles.EnsureSaved(result, change.ObjectName ?? change.ObjectId, target.Permission, target.StoreId, target.ReadAgain);

        AddChanges(change, changed, result, target.DryRun);

        if (!target.DryRun)
        {
            foreach (var version in result.Value?.ThemeFiles ?? [])
            {
                change.VersionTokens[version.Path] = version.ETag;
            }
        }

        change.AddWarning("Customers see this change only after the theme is published (publish_theme), which a second person approves.");
    }

    private static void AddChanges(ThemeFileChange change, List<ThemeFileEdit> changed, SiteBuilderOperationResult<DesignerSaveResult> result, bool dryRun)
    {
        foreach (var edit in changed)
        {
            var saved = dryRun ? GetPreviewContent(result.Preview, edit.Path) ?? edit.Content : edit.Content;
            var prefix = changed.Count > 1 || edit.Path != change.ObjectId ? $"{GetPathInTheme(edit.Path)}: " : string.Empty;
            foreach (var item in Compare(edit.Path, edit.Current.Content, saved))
            {
                change.AddChange(prefix + item.Field, item.From, item.To);
            }
        }
    }

    public static IList<PageDocumentChange> Compare(string path, string before, string after)
    {
        var file = GetPathInTheme(path);
        if (file.Equals(ThemeStorage.TemplateRulesFile, StringComparison.OrdinalIgnoreCase))
        {
            return CompareRules(before, after);
        }

        PageDocumentKind? kind = null;
        if (ThemeTemplateMainSections.GetTemplateKey(file) != null)
        {
            kind = PageDocumentKind.Template;
        }
        else if (file.StartsWith("sections/", StringComparison.OrdinalIgnoreCase))
        {
            kind = PageDocumentKind.SectionGroup;
        }

        if (kind != null &&
            PageDocumentEditor.Read(before ?? EmptyDocument(kind.Value), kind.Value) is { } from &&
            PageDocumentEditor.Read(after ?? EmptyDocument(kind.Value), kind.Value) is { } to)
        {
            return PageDocumentChanges.Compare(from, to);
        }

        return CompareValues(before, after);
    }

    public static string GetPathInTheme(string path) => SiteBuilderContentPaths.GetPathInTheme(path);

    public static string EmptyDocument(PageDocumentKind kind) => kind == PageDocumentKind.SectionGroup ? """{ "content": [] }""" : DefaultPageContent;

    private static string GetPreviewContent(SiteBuilderChangePreview preview, string path) =>
        preview?.Objects
            .FirstOrDefault(x => x.ObjectType == ChangePreview.ThemeFile && string.Equals(x.ObjectId, path, StringComparison.OrdinalIgnoreCase))?
            .Fields.FirstOrDefault(x => x.Name == ChangePreview.Content)?.To;

    private static List<PageDocumentChange> CompareRules(string before, string after)
    {
        var changes = new List<PageDocumentChange>();
        var from = TemplateRulesDocument.Read(before);
        var to = TemplateRulesDocument.Read(after);
        if (from == null || to == null)
        {
            changes.Add(new("template rules", before == null ? null : "stored rules", "new rules"));
            return changes;
        }

        foreach (var type in TemplateRulesDocument.PageTypes)
        {
            var beforeRules = from.RulesOf(type);
            var afterRules = to.RulesOf(type);
            var beforeIds = beforeRules.Select(GetId).ToList();
            var afterIds = afterRules.Select(GetId).ToList();

            foreach (var rule in beforeRules.Where(x => !afterIds.Contains(GetId(x))))
            {
                changes.Add(new($"{type} rule '{GetId(rule)}'", TemplateRuleRow.Describe(rule), "removed"));
            }

            if (!beforeIds.Where(afterIds.Contains).SequenceEqual(afterIds.Where(beforeIds.Contains)))
            {
                changes.Add(new($"order of {type} rules", string.Join(", ", beforeIds), string.Join(", ", afterIds)));
            }

            foreach (var rule in afterRules)
            {
                var previous = beforeRules.FirstOrDefault(x => GetId(x) == GetId(rule));
                if (previous == null)
                {
                    changes.Add(new($"{type} rule '{GetId(rule)}'", null, $"added at position {afterRules.IndexOf(rule)}: {TemplateRuleRow.Describe(rule)}"));
                }
                else if (!JsonNode.DeepEquals(previous, rule))
                {
                    changes.Add(new($"{type} rule '{GetId(rule)}'", TemplateRuleRow.Describe(previous), TemplateRuleRow.Describe(rule)));
                }
            }
        }

        return changes;
    }

    private static List<PageDocumentChange> CompareValues(string before, string after)
    {
        var from = Flatten(Parse(before));
        var to = Flatten(Parse(after));
        var changes = new List<PageDocumentChange>();
        foreach (var key in from.Keys.Concat(to.Keys.Where(x => !from.ContainsKey(x))))
        {
            var oldValue = from.GetValueOrDefault(key);
            var newValue = to.GetValueOrDefault(key);
            if (oldValue != newValue)
            {
                changes.Add(new(key, oldValue, newValue));
            }
        }

        return changes;
    }

    private static JsonNode Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return JsonValue.Create(content);
        }
    }

    private static Dictionary<string, string> Flatten(JsonNode node)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is JsonObject root)
        {
            foreach (var (key, value) in root)
            {
                Flatten(values, key, value);
            }
        }
        else if (node != null)
        {
            values["content"] = node.ToJsonString(PageDocumentEditor.WriteOptions);
        }

        return values;
    }

    private static void Flatten(Dictionary<string, string> values, string path, JsonNode node)
    {
        if (node is JsonObject value && value.Count > 0)
        {
            foreach (var (key, child) in value)
            {
                Flatten(values, $"{path}.{key}", child);
            }

            return;
        }

        values[path] = node == null ? null : GetString(node) ?? node.ToJsonString(PageDocumentEditor.WriteOptions);
    }
}

public sealed class TemplateRuleRow
{
    public string Id { get; init; }

    public string PageType { get; init; }

    public int Position { get; init; }

    public string Template { get; init; }

    public bool Disabled { get; init; }

    public JsonObject When { get; init; }

    public bool IsDefault { get; init; }

    public static IList<TemplateRuleRow> From(TemplateRulesDocument rules) =>
        TemplateRulesDocument.PageTypes
            .SelectMany(type => rules.RulesOf(type).Select((rule, i) => new TemplateRuleRow
            {
                Id = GetId(rule),
                PageType = type,
                Position = i,
                Template = TemplateRulesDocument.GetTemplate(rule),
                Disabled = TemplateRulesDocument.IsDisabled(rule),
                When = rule["when"]?.DeepClone() as JsonObject,
                IsDefault = !rules.HasOwnRules(type),
            }))
            .ToList();

    public static string Describe(JsonObject rule)
    {
        var when = rule["when"] is JsonObject conditions && conditions.Count > 0
            ? $" when {conditions.ToJsonString(PageDocumentEditor.WriteOptions)}"
            : " always";

        return $"{TemplateRulesDocument.GetTemplate(rule)}{when}{(TemplateRulesDocument.IsDisabled(rule) ? " (disabled)" : string.Empty)}";
    }
}

public sealed class ContrastFailureRow
{
    public string Scheme { get; init; }

    public string Pair { get; init; }

    public string Text { get; init; }

    public string TextColor { get; init; }

    public string Background { get; init; }

    public string BackgroundColor { get; init; }

    public double Ratio { get; init; }

    public double MinimumRatio { get; init; }

    public static ContrastFailureRow From(PaletteContrastFailure failure) => new()
    {
        Scheme = failure.Scheme,
        Pair = failure.Pair,
        Text = failure.Text,
        TextColor = failure.TextColor,
        Background = failure.Background,
        BackgroundColor = failure.BackgroundColor,
        Ratio = failure.Ratio,
        MinimumRatio = failure.MinimumRatio,
    };

    public string Describe() =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{Scheme} palette: '{Pair}' has a contrast of {Ratio:0.##}:1 ({Text} {TextColor} on {Background} {BackgroundColor}), less than the {MinimumRatio:0.##}:1 WCAG 2.2 AA asks for");
}
