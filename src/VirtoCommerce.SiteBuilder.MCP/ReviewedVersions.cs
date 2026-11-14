using System.Text.Json;
using ModelContextProtocol;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;

namespace VirtoCommerce.SiteBuilder.MCP;

// A publish, unpublish or discard takes exactly what its dry run showed. An approved change runs again as the approver,
// and a confirmed one may run after a colleague saved: the dry run answers the versions of what the write takes
// (expected_versions), the call sends them back, and the write is refused once they moved. The operation compares them
// again as late as it can, under its lock where it has one.
public static class ReviewedVersions
{
    public const string Argument = "expected_versions";

    public const string Description = "The expected_versions of the dry run, unchanged: the versions of what the call takes, by page " +
        "or file. Required without dryRun, so that exactly what was reviewed is changed.";

    // The condition of [McpRequiresApproval]: a call that names the versions it takes waits for a second person; a call
    // without them is a dry run or is refused by the tool, so nothing is published without approval either way.
    public static bool AreGiven(IReadOnlyDictionary<string, JsonElement> arguments) =>
        arguments != null &&
        arguments.TryGetValue(Argument, out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    // The versions of what the write takes now go into the answer; those the call names must be them. A dry run without
    // them only answers them; a write without them is refused before anything is written.
    public static void Pin(SiteBuilderChange change, IEnumerable<KeyValuePair<string, string>> current, IDictionary<string, string> expected, bool dryRun)
    {
        change.ExpectedVersions = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);

        if (expected == null)
        {
            if (dryRun)
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

    // The version the call named for one item, which the operation compares again.
    public static string Of(SiteBuilderChange change, string key) =>
        change.ExpectedVersions != null && change.ExpectedVersions.TryGetValue(key, out var version) ? version : null;

    public static McpException Changed(SiteBuilderChange change) =>
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

    private static string Subject(SiteBuilderChange change) => change.ObjectType switch
    {
        ChangePreview.ThemeFile => $"'{change.ObjectId}'",
        ChangePreview.MultilanguagePage => $"Multilanguage Page '{change.ObjectId}'",
        _ => $"Page '{change.ObjectId}'",
    };
}
