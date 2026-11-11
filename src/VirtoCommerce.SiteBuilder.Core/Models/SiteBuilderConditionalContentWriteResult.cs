namespace VirtoCommerce.SiteBuilder.Core.Models;

public sealed record SiteBuilderConditionalContentWriteResult(string PageId, string ETag)
{
    public bool ContentWritten { get; init; } = true;
    public bool GroupedEventsPublished { get; init; }
}
