using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.StoreModule.Core.Model;

namespace VirtoCommerce.SiteBuilder.Data.Extensions;

public static class StoreExtensions
{
    public static string GetThemeDefaultLanguage(this Store store)
    {
        return store.DefaultLanguage ?? store.Languages?.FirstOrDefault() ?? ModuleConstants.Theme.FallbackLanguage;
    }
}
