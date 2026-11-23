using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Theme;

public interface IThemeSnapshotBuilder
{
    ThemeSnapshotSet Build(ThemeSource source, JObject catalog, string catalogHash, IList<string> languages, string defaultLanguage, int publication, DateTime publishedDate);
}
