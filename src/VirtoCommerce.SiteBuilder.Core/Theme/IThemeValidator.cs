using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Theme;

public interface IThemeValidator
{
    /// <summary>A null catalog checks the shape only. The source's custom section types count as known types; <see cref="ValidateCustomSections"/> checks their own rules.</summary>
    IList<string> Validate(ThemeSource source, JObject catalog);

    /// <summary>Each custom section type's own rules and their size together. A null catalog refuses any kit tag or behavior.</summary>
    IList<string> ValidateCustomSections(ThemeSource source, JObject catalog);
}
