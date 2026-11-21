using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Theme;

/// <summary>A type's template and CSS are refused, never sanitized, for anything the storefront would leave out.</summary>
public interface ICustomSectionValidator
{
    /// <param name="type">From the file name; an invalid one is an error, not an exception.</param>
    /// <param name="catalog">Its <c>kit</c> lists the kit tags and behaviors a template may use; <c>null</c> refuses all of them.</param>
    CustomSectionValidationResult Validate(string type, JToken content, JObject catalog);

    /// <summary>The rules of each of <paramref name="checkedTypes"/>, and the size of all the <paramref name="types"/> together.</summary>
    /// <param name="types">All the theme's types, by type.</param>
    /// <returns>One result per checked type, and one for the folder when the types together are more than the snapshot carries.</returns>
    IList<CustomSectionValidationResult> ValidateTypes(IDictionary<string, JToken> types, ICollection<string> checkedTypes, JObject catalog);

    /// <summary>The lists <see cref="Validate"/> checks against.</summary>
    CustomSectionRules GetRules();
}
