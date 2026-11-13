namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>What a write would change, read and checked as the write would, with nothing written.</summary>
public class SiteBuilderChangePreview
{
    public IList<SiteBuilderObjectChange> Objects { get; set; } = [];
}
