namespace VirtoCommerce.SiteBuilder.Core.Models;

public class FilePagePublishOperationRequest : FilePageOperationRequest
{
    /// <summary>
    /// Puts the draft on top of what is published before the merge, replacing what changed there since the draft began: the way out of a
    /// merge that conflicts. Only on request, as it throws the other edit away; a conflict is never retried with it.
    /// </summary>
    public bool Rebase { get; set; }
}
