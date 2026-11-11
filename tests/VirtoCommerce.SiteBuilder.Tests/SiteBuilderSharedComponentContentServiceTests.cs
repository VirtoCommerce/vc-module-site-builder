using System;
using Microsoft.EntityFrameworkCore;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.SiteBuilder.Data.Repositories;
using VirtoCommerce.SiteBuilder.Data.Services;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class SiteBuilderSharedComponentContentServiceTests
{
    [Fact]
    public void TouchMetadata_MarksOnlyModifiedDateAndCannotOverwriteConcurrentRename()
    {
        var options = new DbContextOptionsBuilder<SiteBuilderDbContext>()
            .UseSqlServer("Server=(local);Database=SiteBuilderTouchDesignTest;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var context = new SiteBuilderDbContext(options);
        var component = new SiteBuilderSharedComponentEntity
        {
            Id = "component",
            StoreId = "store",
            Name = "name-loaded-before-concurrent-rename",
            CreatedDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ModifiedDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Attach(component);

        SiteBuilderSharedComponentContentService.TouchMetadata(component);
        context.ChangeTracker.DetectChanges();

        var modifiedProperty = Assert.Single(context.Entry(component).Properties, x => x.IsModified);
        Assert.Equal(nameof(SiteBuilderSharedComponentEntity.ModifiedDate), modifiedProperty.Metadata.Name);
        Assert.False(context.Entry(component).Property(x => x.Name).IsModified);
        Assert.False(context.Entry(component).Property(x => x.StoreId).IsModified);
    }
}
