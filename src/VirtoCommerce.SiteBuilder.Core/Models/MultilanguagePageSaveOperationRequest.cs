namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A multilanguage page to create, or to update with the values it holds.</summary>
public class MultilanguagePageSaveOperationRequest
{
    public MultilanguagePage Page { get; set; }

    /// <summary>Checks the write as it would run and answers what it would change instead of changing it.</summary>
    public bool DryRun { get; set; }
}
