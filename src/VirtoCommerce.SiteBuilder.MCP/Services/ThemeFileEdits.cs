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

    // Only on a dry run: what refuses the change.
    [JsonPropertyName("errors")]
    public IList<string> Errors { get; set; }

    // What to do once the change is saved, such as publishing the theme; the warnings are about the change itself.
    [JsonPropertyName("next_step")]
    public string NextStep { get; set; }
}

public sealed record ThemeFileEdit(string Path, ThemeFileContent Current, string Content);

public static class ThemeFileEdits
{
    private const int MaxTextChange = 400;

    // A custom section type's texts, compared by the lines that changed.
    private static readonly string[] SectionTypeTexts = ["template", "css"];

    // The diff is of the content as stored: on a dry run, the sanitized content the save would write. A dry run the save refuses
    // answers the errors in the change, with the diff of the content sent.
    public static async Task SaveAsync(ThemeFileChange change, IDesignerOperationService designerOperationService, ClaimsPrincipal user,
        string storeId, string catalogHash, IList<ThemeFileEdit> edits, bool dryRun, string permission, string readAgain,
        CancellationToken cancellationToken)
    {
        change.DryRun = dryRun;
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
        var result = await ThemeFiles.SaveAsync(designerOperationService, user, storeId, catalogHash, files, dryRun, cancellationToken);
        var refused = dryRun && result.Status == SiteBuilderOperationStatus.Invalid;
        if (refused)
        {
            change.Errors = result.Errors?.Count > 0 ? [.. result.Errors] : [result.Error];
        }
        else
        {
            ThemeFiles.EnsureSaved(result, change.ObjectName ?? change.ObjectId, permission, storeId, readAgain);
        }

        foreach (var edit in changed)
        {
            var saved = dryRun ? GetPreviewContent(result.Preview, edit.Path) ?? edit.Content : edit.Content;
            var prefix = changed.Count > 1 || edit.Path != change.ObjectId ? $"{GetPathInTheme(edit.Path)}: " : string.Empty;
            foreach (var item in Compare(edit.Path, edit.Current.Content, saved))
            {
                change.AddChange(prefix + item.Field, item.From, item.To);
            }
        }

        if (!dryRun)
        {
            foreach (var version in result.Value?.ThemeFiles ?? [])
            {
                change.VersionTokens[version.Path] = version.ETag;
            }
        }

        var warnings = dryRun
            ? result.Preview?.Warnings
            : result.Value?.ThemeFiles.SelectMany(x => x.Warnings.Select(warning => $"{GetPathInTheme(x.Path)}: {warning}"));
        foreach (var warning in warnings ?? [])
        {
            change.AddWarning(warning);
        }

        if (!refused)
        {
            change.NextStep = "Customers see this change only after the theme is published (publish_theme), which a second person approves.";
        }
    }

    public static IList<PageDocumentChange> Compare(string path, string before, string after)
    {
        var file = GetPathInTheme(path);
        if (file.Equals(ThemeStorage.TemplateRulesFile, StringComparison.OrdinalIgnoreCase))
        {
            return CompareRules(before, after);
        }

        if (ThemeCustomSections.GetCustomSectionType(file) != null)
        {
            return CompareSectionType(before, after);
        }

        var kind = ThemeTemplateMainSections.GetTemplateKey(file) != null
            ? PageDocumentKind.Template
            : file.StartsWith("sections/", StringComparison.OrdinalIgnoreCase) ? PageDocumentKind.SectionGroup : (PageDocumentKind?)null;
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

    // Its settings one by one, by their id, and its template and CSS by the lines that changed, so a small edit stays small.
    private static List<PageDocumentChange> CompareSectionType(string before, string after)
    {
        var fromNode = Parse(before);
        var toNode = Parse(after);
        if (fromNode is not (null or JsonObject) || toNode is not (null or JsonObject))
        {
            return CompareValues(before, after);
        }

        var from = fromNode as JsonObject ?? [];
        var to = toNode as JsonObject ?? [];
        var fromSettings = TakeSettings(from);
        var toSettings = TakeSettings(to);
        var fromTexts = SectionTypeTexts.ToDictionary(x => x, x => Take(from, x));
        var toTexts = SectionTypeTexts.ToDictionary(x => x, x => Take(to, x));

        var changes = CompareValues(Flatten(from), Flatten(to));
        changes.AddRange(CompareSettings(fromSettings, toSettings));
        foreach (var text in SectionTypeTexts)
        {
            if (CompareText(text, fromTexts[text], toTexts[text]) is { } change)
            {
                changes.Add(change);
            }
        }

        return changes;
    }

    private static JsonArray TakeSettings(JsonObject type)
    {
        if (type["schema"] is not JsonObject schema)
        {
            return null;
        }

        var settings = schema["settings"] as JsonArray;
        schema.Remove("settings");
        if (schema.Count == 0)
        {
            type.Remove("schema");
        }

        return settings;
    }

    private static string Take(JsonObject type, string key)
    {
        var value = type[key] == null ? null : GetString(type[key]) ?? type[key].ToJsonString(PageDocumentEditor.WriteOptions);
        type.Remove(key);

        return value;
    }

    // A setting without an id, such as a header, is known by its place.
    private static List<PageDocumentChange> CompareSettings(JsonArray before, JsonArray after)
    {
        var from = IndexSettings(before);
        var to = IndexSettings(after);
        var changes = new List<PageDocumentChange>();
        foreach (var key in from.Select(x => x.Key).Union(to.Select(x => x.Key)))
        {
            var oldValue = from.FirstOrDefault(x => x.Key == key).Value?.ToJsonString(PageDocumentEditor.WriteOptions);
            var newValue = to.FirstOrDefault(x => x.Key == key).Value?.ToJsonString(PageDocumentEditor.WriteOptions);
            if (oldValue != newValue)
            {
                changes.Add(new($"schema.settings.{key}", oldValue, newValue));
            }
        }

        var kept = from.Select(x => x.Key).Where(x => to.Any(y => y.Key == x)).ToList();
        var reordered = to.Select(x => x.Key).Where(kept.Contains).ToList();
        if (!kept.SequenceEqual(reordered))
        {
            changes.Add(new("order of schema.settings", string.Join(", ", kept), string.Join(", ", reordered)));
        }

        return changes;
    }

    private static List<KeyValuePair<string, JsonNode>> IndexSettings(JsonArray settings)
    {
        var result = new List<KeyValuePair<string, JsonNode>>();
        for (var i = 0; i < (settings?.Count ?? 0); i++)
        {
            var key = settings[i] is JsonObject field && GetString(field["id"]) is { Length: > 0 } id ? id : $"#{i + 1}";
            if (result.All(x => x.Key != key))
            {
                result.Add(KeyValuePair.Create(key, settings[i]));
            }
        }

        return result;
    }

    // The lines from the first to the last that differ; a new text is all of its lines.
    private static PageDocumentChange CompareText(string field, string before, string after)
    {
        if ((before ?? string.Empty) == (after ?? string.Empty))
        {
            return null;
        }

        var from = (before ?? string.Empty).Split('\n');
        var to = (after ?? string.Empty).Split('\n');
        var start = 0;
        while (start < from.Length && start < to.Length && from[start] == to[start])
        {
            start++;
        }

        var end = 0;
        while (end < from.Length - start && end < to.Length - start && from[^(end + 1)] == to[^(end + 1)])
        {
            end++;
        }

        var last = Math.Max(start + 1, to.Length - end);
        var lines = last > start + 1 ? $"lines {start + 1}-{last}" : $"line {start + 1}";

        return new($"{field} {lines}", before == null ? null : Shorten(string.Join('\n', from[start..^end])),
            after == null ? null : Shorten(string.Join('\n', to[start..^end])));
    }

    private static string Shorten(string text) =>
        text.Length > MaxTextChange ? $"{text[..MaxTextChange]}… ({text.Length - MaxTextChange} more characters)" : text;

    private static List<PageDocumentChange> CompareValues(string before, string after) => CompareValues(Flatten(Parse(before)), Flatten(Parse(after)));

    private static List<PageDocumentChange> CompareValues(Dictionary<string, string> from, Dictionary<string, string> to)
    {
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
