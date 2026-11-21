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
    /// <param name="catalog">
    /// The storefront's schema catalog, whose <c>kit</c> names the kit tags and the behaviors a template may use, as a module of the storefront
    /// may add its own; <c>null</c> refuses every kit tag and behavior.
    /// </param>
    CustomSectionValidationResult Validate(string type, JToken content, JObject catalog);

    /// <summary>The error for types that together are more than the snapshot carries to every page; <c>null</c> for none.</summary>
    /// <param name="types">The file content of each type of the theme by its type.</param>
    string GetSizeError(IDictionary<string, JToken> types);
}
