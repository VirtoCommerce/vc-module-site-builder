using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace VirtoCommerce.SiteBuilder.Core.GitContent;

[JsonConverter(typeof(StringEnumConverter), typeof(CamelCaseNamingStrategy))]
public enum GitHubConnectionSource
{
    Configuration,

    App,
}
