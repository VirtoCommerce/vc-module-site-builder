using System.Reflection;
using Microsoft.EntityFrameworkCore;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.Platform.Data.Infrastructure;

namespace VirtoCommerce.SiteBuilder.Data.Repositories;

public class SiteBuilderDbContext : DbContextBase
{
    public const string SiteBuilderPageTableName = "PageBuilderPage";

    public SiteBuilderDbContext(DbContextOptions<SiteBuilderDbContext> options)
        : base(options)
    {
    }

    protected SiteBuilderDbContext(DbContextOptions options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<GroupedSiteBuilderPageEntity>().ToTable("GroupedPageBuilderPage").HasKey(x => x.Id);
        modelBuilder.Entity<GroupedSiteBuilderPageEntity>().Property(x => x.Id).HasMaxLength(IdLength).ValueGeneratedOnAdd();
        // Restrict: deleting a root must detach its language versions first (IMultilanguagePageService does).
        modelBuilder.Entity<GroupedSiteBuilderPageEntity>().HasOne(x => x.MultilanguagePage).WithMany()
            .HasForeignKey(x => x.MultilanguagePageId).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        modelBuilder.Entity<GroupedSiteBuilderPageEntity>().HasIndex(x => x.MultilanguagePageId);
        // One version per language: the services check it before saving, the index keeps concurrent writers from both passing.
        modelBuilder.Entity<GroupedSiteBuilderPageEntity>().HasIndex(x => new { x.MultilanguagePageId, x.CultureName }).IsUnique();

        modelBuilder.Entity<MultilanguagePageEntity>().ToTable("PageBuilderMultilanguagePage").HasKey(x => x.Id);
        modelBuilder.Entity<MultilanguagePageEntity>().Property(x => x.Id).HasMaxLength(IdLength).ValueGeneratedOnAdd();
        modelBuilder.Entity<MultilanguagePageEntity>().HasIndex(x => x.StoreId);

        modelBuilder.Entity<SiteBuilderPageEntity>().ToTable(SiteBuilderPageTableName).HasKey(x => x.Id);
        modelBuilder.Entity<SiteBuilderPageEntity>().Property(x => x.Id).HasMaxLength(IdLength).ValueGeneratedOnAdd();
        modelBuilder.Entity<SiteBuilderPageEntity>().HasOne(x => x.Group).WithMany(x => x.Pages)
            .HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        modelBuilder.Entity<SiteBuilderPageEntity>().HasOne(x => x.Content).WithOne(x => x.Page)
            .HasForeignKey<SiteBuilderContentEntity>(x => x.Id);
        modelBuilder.Entity<SiteBuilderPageEntity>().Navigation(x => x.Content).AutoInclude(false);

        modelBuilder.Entity<SiteBuilderContentEntity>().ToTable(SiteBuilderPageTableName).HasKey(x => x.Id);

        modelBuilder.Entity<SiteBuilderAssetReferenceEntity>().ToTable("PageBuilderAssetReference").HasKey(x => x.Id);
        modelBuilder.Entity<SiteBuilderAssetReferenceEntity>().Property(x => x.Id).HasMaxLength(IdLength).ValueGeneratedOnAdd();
        modelBuilder.Entity<SiteBuilderAssetReferenceEntity>().HasOne<SiteBuilderPageEntity>().WithMany()
            .HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        modelBuilder.Entity<SiteBuilderAssetReferenceEntity>().HasIndex(x => x.PageId);
        modelBuilder.Entity<SiteBuilderAssetReferenceEntity>().HasIndex(x => x.NormalizedAssetUrlHash);
        modelBuilder.Entity<SiteBuilderAssetReferenceEntity>().HasIndex(x => new { x.PageId, x.NormalizedAssetUrlHash }).IsUnique();

        modelBuilder.Entity<SiteBuilderSharedComponentEntity>().ToTable("PageBuilderSharedComponent").HasKey(x => x.Id);
        modelBuilder.Entity<SiteBuilderSharedComponentEntity>().Property(x => x.Id).HasMaxLength(IdLength).ValueGeneratedOnAdd();
        modelBuilder.Entity<SiteBuilderSharedComponentEntity>().Property(x => x.Name)
            .HasMaxLength(ModuleConstants.SharedComponents.NameMaxLength).IsRequired();
        modelBuilder.Entity<SiteBuilderSharedComponentEntity>().HasIndex(x => x.StoreId);
        modelBuilder.Entity<SiteBuilderSharedComponentEntity>().HasOne(x => x.Content).WithOne(x => x.Component)
            .HasForeignKey<SiteBuilderSharedComponentContentEntity>(x => x.Id)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SiteBuilderSharedComponentEntity>().Navigation(x => x.Content).AutoInclude(false);

        modelBuilder.Entity<SiteBuilderSharedComponentContentEntity>().ToTable("PageBuilderSharedComponentContent").HasKey(x => x.Id);
        modelBuilder.Entity<SiteBuilderSharedComponentContentEntity>().Property(x => x.Id).HasMaxLength(IdLength).ValueGeneratedNever();
        modelBuilder.Entity<SiteBuilderSharedComponentContentEntity>().Property(x => x.ComponentContent).IsRequired();

        modelBuilder.Entity<SiteBuilderSharedComponentReferenceEntity>().ToTable("PageBuilderSharedComponentReference").HasKey(x => x.Id);
        modelBuilder.Entity<SiteBuilderSharedComponentReferenceEntity>().Property(x => x.Id).HasMaxLength(IdLength).ValueGeneratedOnAdd();
        modelBuilder.Entity<SiteBuilderSharedComponentReferenceEntity>().HasOne<SiteBuilderPageEntity>().WithMany()
            .HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        modelBuilder.Entity<SiteBuilderSharedComponentReferenceEntity>().HasOne<SiteBuilderSharedComponentEntity>().WithMany()
            .HasForeignKey(x => x.SharedComponentId).OnDelete(DeleteBehavior.Restrict).IsRequired();
        modelBuilder.Entity<SiteBuilderSharedComponentReferenceEntity>().HasIndex(x => x.PageId);
        modelBuilder.Entity<SiteBuilderSharedComponentReferenceEntity>().HasIndex(x => x.SharedComponentId);
        modelBuilder.Entity<SiteBuilderSharedComponentReferenceEntity>()
            .HasIndex(x => new { x.PageId, x.SharedComponentId }).IsUnique();

        modelBuilder.Entity<SiteBuilderSharedComponentAssetReferenceEntity>()
            .ToTable("PageBuilderSharedComponentAssetReference").HasKey(x => x.Id);
        modelBuilder.Entity<SiteBuilderSharedComponentAssetReferenceEntity>()
            .Property(x => x.Id).HasMaxLength(IdLength).ValueGeneratedOnAdd();
        modelBuilder.Entity<SiteBuilderSharedComponentAssetReferenceEntity>()
            .HasOne<SiteBuilderSharedComponentEntity>().WithMany()
            .HasForeignKey(x => x.SharedComponentId).OnDelete(DeleteBehavior.Cascade).IsRequired();
        modelBuilder.Entity<SiteBuilderSharedComponentAssetReferenceEntity>().HasIndex(x => x.SharedComponentId);
        modelBuilder.Entity<SiteBuilderSharedComponentAssetReferenceEntity>().HasIndex(x => x.NormalizedAssetUrlHash);
        modelBuilder.Entity<SiteBuilderSharedComponentAssetReferenceEntity>()
            .HasIndex(x => new { x.SharedComponentId, x.NormalizedAssetUrlHash }).IsUnique();

        switch (Database.ProviderName)
        {
            case "Pomelo.EntityFrameworkCore.MySql":
                modelBuilder.ApplyConfigurationsFromAssembly(Assembly.Load("VirtoCommerce.SiteBuilder.Data.MySql"));
                break;
            case "Npgsql.EntityFrameworkCore.PostgreSQL":
                modelBuilder.ApplyConfigurationsFromAssembly(Assembly.Load("VirtoCommerce.SiteBuilder.Data.PostgreSql"));
                break;
            case "Microsoft.EntityFrameworkCore.SqlServer":
                modelBuilder.ApplyConfigurationsFromAssembly(Assembly.Load("VirtoCommerce.SiteBuilder.Data.SqlServer"));
                break;
        }
    }
}
