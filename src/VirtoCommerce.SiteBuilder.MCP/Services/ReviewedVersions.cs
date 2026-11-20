using System.Text.Json;
using ModelContextProtocol;
using VirtoCommerce.MCP.Core.Models;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;

namespace VirtoCommerce.SiteBuilder.MCP.Services;

public interface IReviewedChange
{
    IDictionary<string, string> ExpectedVersions { get; set; }
}

public static class ReviewedVersions
{
    public const string Argument = "expected_versions";

    public const string Description = "The expected_versions of the dry run, unchanged: the versions of what the call takes, by page " +
        "or file. Required without dryRun, so that exactly what was reviewed is changed.";

    // Without versions a call is a dry run or is refused, so only a call that names them waits for approval.
    public static bool AreGiven(IReadOnlyDictionary<string, JsonElement> arguments) =>
        arguments != null &&
        arguments.TryGetValue(Argument, out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    // Optional only for a write that needs no approval: it may take the versions of its own dry run.
    public static void Pin<T>(T change, IEnumerable<KeyValuePair<string, string>> current, IDictionary<string, string> expected, bool dryRun,
        bool optional = false)
        where T : McpChangePreview, IReviewedChange
    {
        change.ExpectedVersions = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);

        if (expected == null)
        {
            if (dryRun || optional)
            {
                return;
            }

            throw new McpException($"Nothing was changed: pass {Argument}, the versions the dry run answers, so that exactly what was reviewed " +
                $"is changed. Call {change.Action} with dryRun first.");
        }

        if (!Same(expected, change.ExpectedVersions))
        {
            throw Changed(change);
        }
    }

    public static string Of(IReviewedChange change, string key) =>
        change.ExpectedVersions != null && change.ExpectedVersions.TryGetValue(key, out var version) ? version : null;

    public static void EnsureUnchanged<T>(T change, IEnumerable<KeyValuePair<string, string>> current)
        where T : McpChangePreview, IReviewedChange
    {
        if (!Same(change.ExpectedVersions ?? new Dictionary<string, string>(), new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase)))
        {
            throw Changed(change);
        }
    }

    public static McpException Changed(McpChangePreview change) =>
        new($"{Subject(change)} changed since the dry run that gave {Argument}, so nothing was changed. Call {change.Action} with dryRun " +
            $"again, show the user what it would do now, and send the new {Argument}.");

    private static bool Same(IDictionary<string, string> expected, IDictionary<string, string> current)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in expected)
        {
            normalized[key?.Trim() ?? string.Empty] = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        return normalized.Count == current.Count &&
            current.All(x => normalized.TryGetValue(x.Key, out var version) && string.Equals(version, x.Value, StringComparison.OrdinalIgnoreCase));
    }

    private static string Subject(McpChangePreview change) => change.ObjectType switch
    {
        ChangePreview.ThemeFile => $"'{change.ObjectId}'",
        ChangePreview.MultilanguagePage => $"Multilanguage Page '{change.ObjectId}'",
        ChangePreview.SharedComponent => $"Shared Component '{change.ObjectId}' or a page its sections are copied from",
        "version" => $"A draft that restoring version '{change.ObjectId}' overwrites",
        _ => $"Page '{change.ObjectId}'",
    };
}
