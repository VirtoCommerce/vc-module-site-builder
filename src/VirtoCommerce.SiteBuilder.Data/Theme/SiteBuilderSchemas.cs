using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>How the storefront's schemas are listed and served to the designer and to agents.</summary>
public static class SiteBuilderSchemas
{
    public static bool IsStaticEntry(JToken entry)
    {
        var token = entry?["static"];
        if (token == null || token.Type == JTokenType.Null)
        {
            return false;
        }
        if (token.Type == JTokenType.Boolean)
        {
            return token.Value<bool>();
        }
        if (token.Type == JTokenType.String)
        {
            var value = token.Value<string>();
            return value == "top" || value == "bottom";
        }
        return false;
    }

    public static string MergeStaticSectionsIntoTemplate(string templateJson, IReadOnlyDictionary<string, string> sectionSchemasByKey)
    {
        JObject template;
        try
        {
            template = JObject.Parse(templateJson);
        }
        catch
        {
            return templateJson;
        }

        // Template's `sections` filter (if present and non-empty) restricts which sections apply
        // to this template — for both regular and static. Missing/empty means "all sections".
        HashSet<string> allowedKeys = null;
        if (template["sections"] is JArray sectionsFilter && sectionsFilter.Count > 0)
        {
            allowedKeys = new HashSet<string>(
                sectionsFilter.OfType<JValue>().Select(v => v.Value?.ToString()).Where(s => s != null),
                StringComparer.OrdinalIgnoreCase);
        }

        if (template["settings"] is not JArray templateSettings)
        {
            templateSettings = [];
            template["settings"] = templateSettings;
        }

        foreach (var (sectionKey, sectionJson) in sectionSchemasByKey)
        {
            if (sectionKey.StartsWith('_'))
            {
                continue;
            }
            if (allowedKeys != null && !allowedKeys.Contains(sectionKey))
            {
                continue;
            }
            AppendStaticSectionFields(sectionJson, templateSettings);
        }

        return template.ToString(Formatting.None);
    }

    private static void AppendStaticSectionFields(string sectionJson, JArray templateSettings)
    {
        try
        {
            var sectionSchema = JObject.Parse(sectionJson);
            if (!IsStaticEntry(sectionSchema) || sectionSchema["settings"] is not JArray sectionSettings)
            {
                return;
            }
            foreach (var field in sectionSettings)
            {
                templateSettings.Add(field.DeepClone());
            }
        }
        catch
        {
            // Skip unparseable section files.
        }
    }

    // Upper bound for the catalog's derived `description` summary. Full descriptions average
    // ~1150 chars across a real theme (141 sections ≈ 162 KB); the catalog only needs enough
    // to PICK an entry, so it carries a short head summary instead — see DeriveCatalogSummary.
    public const int CatalogSummaryMaxLength = 200;

    public static void CopySchemaMetadata(JObject source, JObject target, string kind)
    {
        // Catalog metadata is intentionally narrow — only fields the LLM uses while picking.
        // Designer-only hints (`displayField`, `icon`, `tab`, `sort`, `group*`) are stripped.
        // `includeShared` is stripped — it matters during field-list resolution in Phase B
        // and the agent reads it from the full schema response there.
        // `static` is stripped — static sections are entirely hidden from the agent (their
        // `settings[]` is merged into the template response instead).
        // `description` is exposed only for kinds where the agent picks an entry by intent
        // (regular sections, templates, blocks). For objects/shared descriptions add noise.
        // It is shortened to a head summary here; the full description is served by
        // GetSchemaByKey (Phase B) once the agent commits to an entry.
        var includeDescription = kind is ModuleConstants.SchemaKinds.Sections or ModuleConstants.SchemaKinds.Templates or ModuleConstants.SchemaKinds.Blocks;
        var properties = includeDescription
            ? new[] { "name", "description" }
            : new[] { "name" };

        foreach (var property in properties)
        {
            var token = source[property];
            if (token == null || token.Type == JTokenType.Null)
            {
                continue;
            }

            target[property] = property == "description" && token.Type == JTokenType.String
                ? DeriveCatalogSummary(token.Value<string>())
                : token.DeepClone();
        }
    }

    /// <summary>
    /// Reduces a full schema description to a short head summary for the catalog listing.
    /// Theme descriptions follow a "&lt;what it is&gt; Use when: … Skip when: … &lt;field notes&gt;"
    /// structure; the head before "Use when:" is the selection signal. The verbose remainder
    /// (guidance + per-field notes, which duplicate the schema's <c>settings[]</c>) is dropped
    /// from the catalog and served in full by GetSchemaByKey when an entry is actually used.
    /// </summary>
    public static string DeriveCatalogSummary(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        var marker = description.IndexOf("Use when", StringComparison.OrdinalIgnoreCase);
        var head = (marker > 0 ? description[..marker] : description).Trim();
        if (head.Length == 0)
        {
            head = description.Trim();
        }

        if (head.Length <= CatalogSummaryMaxLength)
        {
            return head;
        }

        // Prefer cutting at the last sentence boundary within the cap; else hard-cut + ellipsis.
        var slice = head[..CatalogSummaryMaxLength];
        var lastStop = slice.LastIndexOfAny(['.', '!', '?']);
        return lastStop >= CatalogSummaryMaxLength / 2
            ? slice[..(lastStop + 1)]
            : slice.TrimEnd() + "…";
    }
}
