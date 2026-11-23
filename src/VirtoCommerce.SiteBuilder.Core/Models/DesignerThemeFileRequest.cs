namespace VirtoCommerce.SiteBuilder.Core.Models;

public class DesignerThemeFileRequest : SiteBuilderOperationRequest
{
    /// <summary>The theme folder and the file path: "default/sections/header-group.json".</summary>
    public string Path { get; set; }
}
