namespace VirtoCommerce.SiteBuilder.Core.Models;

public class ThemePublishOperationRequest : SiteBuilderOperationRequest
{
    /// <summary>The section schema catalog the designer previewed; the theme is checked against it.</summary>
    public string CatalogHash { get; set; }

    /// <summary>Checks the theme as a publication does, and answers the drafts it would publish instead of publishing them.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// The version of every draft the caller reviewed, by its path with the theme folder, as a dry run answers it. The publication is refused
    /// when the drafts are not those any more: one changed, appeared or went. <c>null</c> publishes the drafts whatever they hold.
    /// </summary>
    public IDictionary<string, string> ExpectedVersions { get; set; }
}
