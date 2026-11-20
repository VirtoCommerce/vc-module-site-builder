using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>
/// Keeps content that can run script in a storefront out of storage: refuses script URLs and palette values that are not colors,
/// and sanitizes the HTML the content stores, in place.
/// </summary>
public interface ISiteBuilderContentGuard
{
    /// <summary>A page, a shared component or a template: <c>{ settings, content }</c> or the legacy array.</summary>
    /// <param name="name">Names the content in the errors.</param>
    /// <param name="catalog">The section schema catalog; null applies only the checks that need no schema.</param>
    SiteBuilderContentGuardResult GuardPage(JToken document, string name, JObject catalog);

    /// <param name="file">The path in the theme folder: "templates/product.json".</param>
    SiteBuilderContentGuardResult GuardThemeFile(string file, JToken content, JObject catalog);
}
