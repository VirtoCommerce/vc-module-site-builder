namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A page group to create with a draft that already has its content.</summary>
public class GroupedPageCreateOperationRequest
{
    public CreateGroupedPageRequest Page { get; set; }

    /// <summary>Checks the write as it would run and answers what it would change instead of changing it.</summary>
    public bool DryRun { get; set; }
}
