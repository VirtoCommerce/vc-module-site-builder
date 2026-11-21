using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Theme;

public interface IThemeValidator
{
    /// <summary>A null catalog checks the shape only. A custom section type the source holds is a known type, but its own rules are not checked.</summary>
    IList<string> Validate(ThemeSource source, JObject catalog);

    /// <summary>
    /// The rules of each custom section type the source holds, and the size of the types together: a publication checks every type it
    /// publishes, while a save checks the types it writes. A null catalog refuses a kit tag or a behavior any of them uses.
    /// </summary>
    IList<string> ValidateCustomSections(ThemeSource source, JObject catalog);
}
