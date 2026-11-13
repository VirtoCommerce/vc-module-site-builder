namespace VirtoCommerce.SiteBuilder.Core.Models;

public class MultilanguagePageOperationRequest
{
    public string MultilanguagePageId { get; set; }

    /// <summary>Checks the write as it would run and answers what it would change instead of changing it.</summary>
    public bool DryRun { get; set; }
}
