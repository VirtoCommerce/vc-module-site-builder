namespace VirtoCommerce.SiteBuilder.Core.Models;

public class MultilanguagePageLanguageOperationRequest : MultilanguagePageOperationRequest
{
    public string CultureName { get; set; }

    /// <summary>The new version starts from the content of the default language version.</summary>
    public bool CopyContent { get; set; } = true;
}
