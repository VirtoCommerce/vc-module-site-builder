namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>What discarding a draft would do, read with nothing written.</summary>
public class DraftDiscardPreview
{
    public DraftDiscardStatus Status { get; set; }

    /// <summary><c>null</c> when there is no draft.</summary>
    public string DraftContent { get; set; }

    /// <summary>What the file is once the draft is gone; <c>null</c> when nothing is published.</summary>
    public string PublishedContent { get; set; }
}
