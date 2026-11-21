using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Theme;

public interface IThemeValidator
{
    /// <summary>A null catalog checks the shape only, and the custom section types, whose rules need no catalog.</summary>
    IList<string> Validate(ThemeSource source, JObject catalog);
}
