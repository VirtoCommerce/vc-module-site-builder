using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.GitContent
{
    /// <summary>
    /// Structural validation of a .page document before it is committed to git — the C# port of
    /// the ERROR-level checks of vc-content tools/validate-page.mjs that need no theme schemas
    /// (envelope shape, settings.type, sections are typed objects, unique ids). Schema-aware
    /// checks (unknown section type etc.) stay in the PR CI gate, which runs the full validator.
    /// </summary>
    public static class PageEnvelopeValidator
    {
        /// <summary>
        /// Returns the list of errors; empty means the document is committable.
        /// Reads both the {settings, content} envelope and the legacy flat array.
        /// </summary>
        public static IList<string> Validate(JToken page)
        {
            var errors = new List<string>();

            if (!TryReadEnvelope(page, out var settings, out var content))
            {
                errors.Add("Page is neither a {settings, content} envelope nor a legacy array.");
                return errors;
            }

            ValidateSettings(settings, errors);

            if (content is not JArray sections)
            {
                errors.Add("Missing content array (sections).");
                return errors;
            }

            var seenIds = new Dictionary<string, int>();
            for (var i = 0; i < sections.Count; i++)
            {
                if (sections[i] is JObject sectionObject)
                {
                    ValidateSection(sectionObject, i, seenIds, errors);
                }
                else
                {
                    errors.Add($"content[{i}]: section is not an object.");
                }
            }

            return errors;
        }

        private static bool TryReadEnvelope(JToken page, out JToken settings, out JToken content)
        {
            switch (page)
            {
                // legacy flat array: [0] = settings, the rest = sections
                case JArray array:
                    settings = array.Count > 0 ? array[0] : null;
                    content = new JArray(array.Skip(1));
                    return true;
                case JObject envelope:
                    settings = envelope["settings"];
                    content = envelope["content"];
                    return true;
                default:
                    settings = null;
                    content = null;
                    return false;
            }
        }

        private static void ValidateSettings(JToken settings, List<string> errors)
        {
            if (settings is not JObject settingsObject)
            {
                errors.Add("Missing settings object (page-level settings).");
            }
            else if (TextOf(settingsObject["type"]) != "settings")
            {
                errors.Add($"settings.type must be \"settings\" (got: {settingsObject["type"]?.ToString() ?? "null"}).");
            }
        }

        private static void ValidateSection(JObject section, int index, Dictionary<string, int> seenIds, List<string> errors)
        {
            var type = TextOf(section["type"]);
            var label = $"content[{index}]{(string.IsNullOrEmpty(type) ? string.Empty : $" ({type})")}";

            if (string.IsNullOrEmpty(type))
            {
                errors.Add($"{label}: missing type field.");
            }

            var id = TextOf(section["id"]);
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            if (seenIds.TryGetValue(id, out var firstIndex))
            {
                errors.Add($"{label}: duplicate id \"{id}\" (already in content[{firstIndex}]).");
            }
            else
            {
                seenIds.Add(id, index);
            }
        }

        // The document comes from whoever saves it: an object or an array where a value belongs reads as
        // missing instead of throwing.
        private static string TextOf(JToken token) => token is JValue value ? (string)value : null;
    }
}
