namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderFieldChange
{
    public string Name { get; set; }

    /// <summary><c>null</c> when there is nothing yet.</summary>
    public string From { get; set; }

    public string To { get; set; }
}
