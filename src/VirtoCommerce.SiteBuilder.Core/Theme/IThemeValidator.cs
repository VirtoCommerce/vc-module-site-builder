using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Theme;

public interface IThemeValidator
{
    /// <summary>A null catalog checks the shape only, and the custom section types, refusing a kit tag or a behavior any of them uses.</summary>
    IList<string> Validate(ThemeSource source, JObject catalog);
}
