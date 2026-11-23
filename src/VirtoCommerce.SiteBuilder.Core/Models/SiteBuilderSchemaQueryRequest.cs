namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderSchemaQueryRequest : SiteBuilderThemeQueryRequest
{
    /// <summary>One of <see cref="ModuleConstants.SchemaKinds"/>.</summary>
    public string Kind { get; set; }

    public string Key { get; set; }
}
