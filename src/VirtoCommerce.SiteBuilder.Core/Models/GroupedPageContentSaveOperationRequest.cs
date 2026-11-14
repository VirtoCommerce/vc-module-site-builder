namespace VirtoCommerce.SiteBuilder.Core.Models;

public class GroupedPageContentSaveOperationRequest : GroupedPageOperationRequest
{
    public string Content { get; set; }

    /// <summary>
    /// Reads the content in place of <see cref="Content"/>, for a raw save only, and only once the page and the user's access to it
    /// are checked: a refused request is answered without its content being read.
    /// </summary>
    public Func<CancellationToken, Task<string>> ContentReader { get; set; }

    /// <summary>The version of the content the caller read.</summary>
    public string ETag { get; set; }

    /// <summary>The version the caller read, sent apart from the content (an If-Match header); it must name the same version as <see cref="ETag"/>.</summary>
    public string IfMatch { get; set; }

    public string SaveId { get; set; }
}
