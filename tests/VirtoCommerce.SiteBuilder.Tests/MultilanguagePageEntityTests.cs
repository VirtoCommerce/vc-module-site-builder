using System;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Models;
using VirtoCommerce.Platform.Core.Common;
using Xunit;

namespace VirtoCommerce.SiteBuilder.Tests;

public class MultilanguagePageEntityTests
{
    [Fact]
    public void MultilanguagePageEntity_RoundTripsEveryField_PatchKeepsStore()
    {
        var model = new MultilanguagePage
        {
            Id = "root",
            StoreId = "store",
            Name = "FAQ",
            Permalink = "/faq",
            DefaultCultureName = "nl-NL",
            OrganizationId = "org",
            Visibility = true,
            UserGroups = "vip",
            StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        };

        var entity = new MultilanguagePageEntity().FromModel(model, new PrimaryKeyResolvingMap());
        // The stored root keeps its store: a multilanguage page never moves to another one.
        var target = new MultilanguagePageEntity { Id = "root", StoreId = "stored-store" };
        entity.Patch(target);
        var restored = target.ToModel(new MultilanguagePage());

        Assert.Equal(model.StoreId, entity.ToModel(new MultilanguagePage()).StoreId);
        Assert.Equal(
            ("stored-store", model.Name, model.Permalink, model.DefaultCultureName, model.OrganizationId, model.Visibility, model.UserGroups, model.StartDate, model.EndDate),
            (restored.StoreId, restored.Name, restored.Permalink, restored.DefaultCultureName, restored.OrganizationId, restored.Visibility, restored.UserGroups, restored.StartDate, restored.EndDate));
    }

    [Fact]
    public void GroupEntityPatch_NeverChangesLink_KeepsMaskAndRevisionWhenNotSent()
    {
        var revision = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var stored = new GroupedSiteBuilderPageEntity
        {
            MultilanguagePageId = "root",
            OverriddenFields = "Permalink",
            SourceRevisionDate = revision,
        };
        var unawareClient = new GroupedSiteBuilderPageEntity
        {
            Name = "FAQ",
            MultilanguagePageId = null,
            OverriddenFields = null,
            SourceRevisionDate = null,
        };

        unawareClient.Patch(stored);

        Assert.Equal(("root", "Permalink", revision, "FAQ"), (stored.MultilanguagePageId, stored.OverriddenFields, stored.SourceRevisionDate, stored.Name));

        var later = revision.AddDays(1);
        new GroupedSiteBuilderPageEntity { MultilanguagePageId = "other", OverriddenFields = string.Empty, SourceRevisionDate = later }.Patch(stored);

        Assert.Equal(("root", string.Empty, later), (stored.MultilanguagePageId, stored.OverriddenFields, stored.SourceRevisionDate));
    }

    [Fact]
    public void GroupEntity_RoundTripsMultilanguageFields()
    {
        var revision = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var model = new GroupedSiteBuilderPage
        {
            Id = "group",
            MultilanguagePageId = "root",
            OverriddenFields = "Permalink,Visibility",
            SourceRevisionDate = revision,
            Pages = [],
        };

        var restored = new GroupedSiteBuilderPageEntity().FromModel(model, new PrimaryKeyResolvingMap()).ToModel(new GroupedSiteBuilderPage());

        Assert.Equal(("root", "Permalink,Visibility", revision), (restored.MultilanguagePageId, restored.OverriddenFields, restored.SourceRevisionDate));
    }
}
