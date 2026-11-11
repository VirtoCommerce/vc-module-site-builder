using VirtoCommerce.AssetsModule.Core.Assets;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderAssetSearchResult : BlobEntrySearchResult
{
    public int FileCount { get; set; }

    public int Skip { get; set; }
}
