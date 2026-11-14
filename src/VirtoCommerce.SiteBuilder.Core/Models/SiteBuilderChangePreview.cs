namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>What a write would change, read and checked as the write would, with nothing written.</summary>
public class SiteBuilderChangePreview
{
    public IList<SiteBuilderObjectChange> Objects { get; set; } = [];

    /// <summary>The write would reach what the storefront serves; see <see cref="SiteBuilderOperationResult.AffectsPublished"/>.</summary>
    public bool AffectsPublished { get; set; }

    /// <summary>What the caller should know before approving the write, such as an author the history does not record.</summary>
    public IList<string> Warnings { get; set; } = [];
}
