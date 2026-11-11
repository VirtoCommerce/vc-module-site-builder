using VirtoCommerce.SiteBuilder.Data.Repositories;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderWriteLockTests
{
    [Fact]
    public void OrderIds_DeduplicatesAndUsesStableGlobalOrder()
    {
        var first = SiteBuilderWriteLock.OrderIds(["page-z", "Page-a", "page-b", "page-a", null, ""]);
        var second = SiteBuilderWriteLock.OrderIds(["page-b", "page-a", "page-z"]);

        Assert.Equal(["Page-a", "page-b", "page-z"], first);
        Assert.Equal(["page-a", "page-b", "page-z"], second);
    }
}
