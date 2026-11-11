using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderAssetReferencesSearchCriteria : SearchCriteriaBase, IHasStoreId
{
    public string StoreId { get; set; }

    public string Statuses { get; set; }

    public bool IncludePages { get; set; }

    public string FolderUrl { get; set; }

    public IList<string> AssetUrls { get; set; } = [];
}
