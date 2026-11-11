namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderAssetReference
{
    public string AssetUrl { get; set; }

    public string NormalizedAssetUrl { get; set; }

    public int ReferencesCount { get; set; }

    public int PageReferencesCount { get; set; }

    public int SharedComponentReferencesCount { get; set; }

    public IList<SiteBuilderAssetReferencePage> Pages { get; set; } = [];

    public IList<SiteBuilderAssetReferenceSharedComponent> SharedComponents { get; set; } = [];
}
