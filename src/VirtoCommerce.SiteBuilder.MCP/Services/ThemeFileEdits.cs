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

    // What to do once the change is saved, such as publishing the theme.
    [JsonPropertyName("next_step")]
    public string NextStep { get; set; }

    // A section type's settings added or removed, and the pages whose sections of the type they reach.
    [JsonPropertyName("placed_sections")]
    public SectionTypePlacements PlacedSections { get; set; }
}

public sealed class SectionTypePlacements
{
    [JsonPropertyName("new_settings")]
    public IList<NewSettingRow> NewSettings { get; init; } = [];

    [JsonPropertyName("removed_settings")]
    public IList<string> RemovedSettings { get; init; } = [];

    [JsonPropertyName("pages")]
    public IList<PlacingPageRow> Pages { get; init; } = [];

    [JsonPropertyName("coverage")]
    public string Coverage { get; init; }

    [JsonPropertyName("note")]
    public string Note { get; init; }
}

public sealed record NewSettingRow([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("placed_sections_show")] string PlacedSectionsShow);

// sections counts only the page's own: edit_page_draft cannot set those of a placed Shared Component.
public sealed record PlacingPageRow([property: JsonPropertyName("page_id")] string PageId, [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("permalink")] string Permalink,
    [property: JsonPropertyName("sections")] int Sections, [property: JsonPropertyName("shared_component_ids")] IList<string> SharedComponentIds)
{
    // publish_theme's dry run only: whether the page's published version or its draft places the type
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Version { get; init; }
}

public sealed record ThemeFileEdit(string Path, ThemeFileContent Current, string Content);

public sealed record ThemeFileSaveTarget(string StoreId, string CatalogHash, bool DryRun, string Permission, string ReadAgain);

public static class ThemeFileEdits
{
    private const int MaxTextChange = 400;

    // A custom section type's texts, compared by the lines that changed.
    private static readonly string[] SectionTypeTexts = [ThemeCustomSections.TemplateKey, ThemeCustomSections.CssKey];

    // The diff is of the content as stored: on a dry run, the sanitized content the save would write. A dry run the save refuses
    // answers the errors in the change, with the diff of the content sent.
    public static async Task SaveAsync(ThemeFileChange change, IDesignerOperationService designerOperationService, ClaimsPrincipal user,
        ThemeFileSaveTarget target, IList<ThemeFileEdit> edits, CancellationToken cancellationToken)
    {
        change.DryRun = target.DryRun;
        foreach (var edit in edits)
        {
            change.VersionTokens[edit.Path] = edit.Current.ETag;
        }

        var changed = edits.Where(x => !IsSameContent(x.Path, x.Current.Content, x.Content)).ToList();
        if (changed.Count == 0)
        {
            change.AddWarning("The call changes nothing, so nothing was saved.");
            if (!target.DryRun)
            {
                return;
            }
        }

        // a dry run that changes nothing still answers what the file breaks
        var files = (changed.Count > 0 ? changed : edits).Select(x => new DesignerSaveFile { Path = x.Path, Content = x.Content, ETag = x.Current.ETag }).ToList();
        var result = await ThemeFiles.SaveAsync(designerOperationService, user, target.StoreId, target.CatalogHash, files, target.DryRun, cancellationToken);
        AddResult(change, changed, result, target);
    }

    private static void AddResult(ThemeFileChange change, List<ThemeFileEdit> changed, SiteBuilderOperationResult<DesignerSaveResult> result, ThemeFileSaveTarget target)
    {
        var refused = target.DryRun && result.Status == SiteBuilderOperationStatus.Invalid;
        if (refused)
        {
            change.Errors = result.Errors?.Count > 0 ? [.. result.Errors] : [result.Error];
        }
        else
        {
            ThemeFiles.EnsureSaved(result, change.ObjectName ?? change.ObjectId, target.Permission, target.StoreId, target.ReadAgain);
        }

        AddChanges(change, changed, result, target.DryRun);

        if (!target.DryRun)
        {
            AddVersionTokens(change, result.Value);
        }

        AddWarnings(change, result, target.DryRun);

        if (!refused && changed.Count > 0)
        {
            change.NextStep = "Customers see this change only after the theme is published (publish_theme), which a second person approves.";
        }
    }

    private static void AddVersionTokens(ThemeFileChange change, DesignerSaveResult saved)
    {
        foreach (var version in saved?.ThemeFiles ?? [])
        {
            change.VersionTokens[version.Path] = version.ETag;
        }
    }

    private static void AddWarnings(ThemeFileChange change, SiteBuilderOperationResult<DesignerSaveResult> result, bool dryRun)
    {
        var warnings = dryRun
            ? result.Preview?.Warnings
            : result.Value?.ThemeFiles.SelectMany(x => x.Warnings.Select(warning => $"{GetPathInTheme(x.Path)}: {warning}"));
        foreach (var warning in warnings ?? [])
        {
            change.AddWarning(warning);
        }
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

    // A readable diff leaves out what it cannot show, so whether the content changed is decided on the JSON itself. A file that is not
    // there is read as the diff reads it, empty: an edit that leaves it empty, such as the palette of the other scheme, does not create it.
    private static bool IsSameContent(string path, string before, string after)
    {
        if (before == null)
        {
            return Compare(path, before, after).Count == 0;
        }

        return Parse(before) is { } from && Parse(after) is { } to ? JsonNode.DeepEquals(from, to) : string.Equals(before, after, StringComparison.Ordinal);
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
        // settings that are not a list on either side are compared as a value
        var lists = HasSettingsList(from) && HasSettingsList(to);
        var fromSettings = lists ? TakeSettings(from) : null;
        var toSettings = lists ? TakeSettings(to) : null;
        var fromTexts = SectionTypeTexts.ToDictionary(x => x, x => Take(from, x));
        var toTexts = SectionTypeTexts.ToDictionary(x => x, x => Take(to, x));

        var changes = CompareValues(Flatten(from), Flatten(to));
        changes.AddRange(CompareSettings(fromSettings, toSettings));
        changes.AddRange(SectionTypeTexts.Select(x => CompareText(x, fromTexts[x], toTexts[x])).Where(x => x != null));

        return changes;
    }

    private static bool HasSettingsList(JsonObject type) => (type[ThemeCustomSections.SchemaKey] as JsonObject)?["settings"] is null or JsonArray;

    private static JsonArray TakeSettings(JsonObject type)
    {
        if (type[ThemeCustomSections.SchemaKey] is not JsonObject schema)
        {
            return [];
        }

        var settings = schema["settings"] as JsonArray ?? [];
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
        var start = CountSameLines(from, to);
        var end = CountSameLines(from.Skip(start).Reverse(), to.Skip(start).Reverse());
        var last = Math.Max(start + 1, to.Length - end);
        var lines = last > start + 1 ? $"lines {start + 1}-{last}" : $"line {start + 1}";

        return new($"{field} {lines}", before == null ? null : Shorten(string.Join('\n', from[start..^end])),
            after == null ? null : Shorten(string.Join('\n', to[start..^end])));
    }

    private static int CountSameLines(IEnumerable<string> from, IEnumerable<string> to) => from.Zip(to).TakeWhile(x => x.First == x.Second).Count();

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
