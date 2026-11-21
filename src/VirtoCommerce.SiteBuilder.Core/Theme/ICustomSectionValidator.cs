using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Theme;

/// <summary>
/// Checks the custom section types of a theme, each kept as <c>custom-sections/{type}.json</c>: a type runs no code of its own, so its
/// template and CSS are refused for anything the storefront would have to leave out, never sanitized.
/// </summary>
public interface ICustomSectionValidator
{
    /// <summary>The errors and the warnings name the file.</summary>
    /// <param name="type">The type as its file names it, valid or not.</param>
    CustomSectionValidationResult Validate(string type, JToken content);

    /// <summary>The error for types that together are more than the snapshot carries to every page; <c>null</c> for none.</summary>
    /// <param name="types">The file content of each type of the theme by its type.</param>
    string GetSizeError(IDictionary<string, JToken> types);
}
