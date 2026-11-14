using ModelContextProtocol;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants;

namespace VirtoCommerce.SiteBuilder.MCP;

// The page and section filters the page tools share, read from the agent's arguments.
public static class SitePageFilters
{
    public const string DraftVersion = "draft";
    public const string PublishedVersion = "published";

    public static string ParseStatuses(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        string[] known = [PageStatuses.Draft, PageStatuses.Published, PageStatuses.Archived];
        var statuses = status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => known.FirstOrDefault(y => y.EqualsIgnoreCase(x))
                ?? throw new McpException($"Status '{x}' is unknown. Use {string.Join(", ", known)}."))
            .Distinct();

        return string.Join(',', statuses);
    }

    public static string ParsePageType(string pageType)
    {
        if (string.IsNullOrWhiteSpace(pageType))
        {
            return null;
        }

        string[] known = [PageTypes.Landing, PageTypes.Multilanguage];

        return known.FirstOrDefault(x => x.EqualsIgnoreCase(pageType.Trim()))
            ?? throw new McpException($"Page type '{pageType}' is unknown. Use {string.Join(" or ", known)}.");
    }

    // true for the published version.
    public static bool ParseVersion(string version) => (version ?? DraftVersion).ToLowerInvariant() switch
    {
        DraftVersion => false,
        PublishedVersion => true,
        _ => throw new McpException("version must be 'draft' or 'published'."),
    };

    // Null without a path; one of equals and contains goes with it.
    public static SiteBuilderSectionSettingCondition ParseCondition(string path, string equals, string contains, string language)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            if (equals != null || contains != null || language != null)
            {
                throw new McpException("setting_equals, setting_contains and setting_language go with setting_path, the setting they compare.");
            }

            return null;
        }

        if ((equals == null) == (contains == null))
        {
            throw new McpException("Pass one of setting_equals and setting_contains with setting_path.");
        }

        var condition = new SiteBuilderSectionSettingCondition
        {
            Path = path.Trim(),
            Operator = equals != null ? SiteBuilderSectionSettingCondition.EqualsOperator : SiteBuilderSectionSettingCondition.ContainsOperator,
            Value = equals ?? contains,
            Language = string.IsNullOrWhiteSpace(language) ? null : language.Trim(),
        };

        if (SiteBuilderSectionConditions.Validate(condition) is { } error)
        {
            throw new McpException(error);
        }

        return condition;
    }
}
