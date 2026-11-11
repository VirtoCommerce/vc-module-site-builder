using System.Collections.Generic;
using System.IO;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Services;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderSharedComponentServiceTests
{
    [Fact]
    public void ValidateAndNormalize_TrimsNameAndRejectsNamesOverSharedLimit()
    {
        var valid = new SiteBuilderSharedComponent
        {
            StoreId = "store",
            Name = $"  {new string('x', ModuleConstants.SharedComponents.NameMaxLength)}  ",
        };

        SiteBuilderSharedComponentService.ValidateAndNormalize(valid);

        Assert.Equal(ModuleConstants.SharedComponents.NameMaxLength, valid.Name.Length);
        Assert.Throws<InvalidDataException>(() =>
            SiteBuilderSharedComponentService.ValidateAndNormalize(new SiteBuilderSharedComponent
            {
                StoreId = "store",
                Name = new string('x', ModuleConstants.SharedComponents.NameMaxLength + 1),
            }));
    }

    [Fact]
    public void ValidateStoreImmutability_RejectsMovingExistingComponent()
    {
        var component = new SiteBuilderSharedComponent
        {
            Id = "component",
            StoreId = "new-store",
            Name = "Shared header",
        };

        var exception = Assert.Throws<InvalidDataException>(() =>
            SiteBuilderSharedComponentService.ValidateStoreImmutability(
                [component],
                new Dictionary<string, string> { [component.Id] = "original-store" }));

        Assert.Contains("cannot be moved", exception.Message);
    }
}
