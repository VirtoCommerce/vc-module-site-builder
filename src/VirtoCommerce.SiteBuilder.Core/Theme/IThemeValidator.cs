using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Theme;

public interface IThemeValidator
{
    /// <summary>A null catalog checks the shape only. The source's custom section types count as known types, without their own rules.</summary>
    IList<string> Validate(ThemeSource source, JObject catalog);
}
