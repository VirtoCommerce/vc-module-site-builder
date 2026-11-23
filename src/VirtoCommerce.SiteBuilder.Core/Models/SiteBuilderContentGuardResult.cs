namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderContentGuardResult
{
    /// <summary>One per value refused, naming the content and the JSON path.</summary>
    public IList<string> Errors { get; set; } = new List<string>();

    /// <summary>The content was changed in place: some HTML was sanitized.</summary>
    public bool Changed { get; set; }
}
