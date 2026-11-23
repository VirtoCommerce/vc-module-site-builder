namespace VirtoCommerce.SiteBuilder.Core.Models;

public class DesignerDiscardOperationRequest : SiteBuilderOperationRequest
{
    public DesignerDiscardRequest Changes { get; set; }

    public string SaveId { get; set; }

    /// <summary>Checks the discard as it would run and answers the drafts it would throw away instead of throwing them away.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// The version of the page's draft the caller reviewed, as a dry run answers it in <see cref="SiteBuilderObjectChange.ETag"/>: the discard is
    /// refused once the draft is at another one, or gone. <c>null</c> discards the draft whatever it holds.
    /// </summary>
    public string ExpectedPageVersion { get; set; }

    /// <summary>
    /// The version of every theme file draft the caller reviewed, by its path with the theme folder, as a dry run answers them. The discard is
    /// refused when the drafts to discard are not those any more: one changed, appeared or went. <c>null</c> discards them whatever they hold.
    /// </summary>
    public IDictionary<string, string> ExpectedThemeFileVersions { get; set; }
}
