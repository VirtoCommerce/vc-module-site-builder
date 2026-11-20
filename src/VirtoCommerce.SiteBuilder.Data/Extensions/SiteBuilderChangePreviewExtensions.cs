using System.Globalization;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.ChangePreview;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.PageStatuses;

namespace VirtoCommerce.SiteBuilder.Data.Extensions;

public static class SiteBuilderChangePreviewExtensions
{
    public static SiteBuilderObjectChange AddObject(this SiteBuilderChangePreview preview, string objectType, string objectId, string eTag = null)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var change = AbstractTypeFactory<SiteBuilderObjectChange>.TryCreateInstance();
        change.ObjectType = objectType;
        change.ObjectId = objectId;
        change.ETag = eTag;
        preview.Objects.Add(change);

        return change;
    }

    /// <summary>Adds the field only when its value changes. A missing text and an empty one are the same value, as the stores read them.</summary>
    public static SiteBuilderObjectChange AddField(this SiteBuilderObjectChange change, string name, object from, object to)
    {
        ArgumentNullException.ThrowIfNull(change);

        var fromText = Format(from);
        var toText = Format(to);
        if (string.Equals(fromText ?? string.Empty, toText ?? string.Empty, StringComparison.Ordinal))
        {
            return change;
        }

        var field = AbstractTypeFactory<SiteBuilderFieldChange>.TryCreateInstance();
        field.Name = name;
        field.From = fromText;
        field.To = toText;
        change.Fields.Add(field);

        return change;
    }

    /// <summary>Adds the settings of the page group as the write leaves them; <paramref name="from"/> is <c>null</c> for a page it creates.</summary>
    /// <returns>The change, or <c>null</c> when nothing of the page changes.</returns>
    public static SiteBuilderObjectChange AddPage(this SiteBuilderChangePreview preview, GroupedSiteBuilderPage from, GroupedSiteBuilderPage to)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(to);

        var change = AbstractTypeFactory<SiteBuilderObjectChange>.TryCreateInstance();
        change.ObjectType = Page;
        change.ObjectId = from?.Id;
        AddPageNaming(change, from, to);
        AddPageAudience(change, from, to);
        AddPageLanguage(change, from, to);
        if (change.Fields.Count == 0)
        {
            return null;
        }

        preview.Objects.Add(change);
        // what a published page holds is served as soon as it is stored, its settings included
        preview.AffectsPublished |= from?.Status == Published;

        return change;
    }

    /// <summary>Adds a publish of the draft of the page (Draft to Published), or an unpublish of its published page (Published to Draft).</summary>
    public static SiteBuilderObjectChange AddPublishing(this SiteBuilderChangePreview preview, GroupedSiteBuilderPage group, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(group);

        preview.AffectsPublished = true;

        return preview.AddObject(Page, group.Id).AddField(Status, from, to);
    }

    /// <summary>Adds the multilanguage page as the write leaves it; <paramref name="from"/> is <c>null</c> for one it creates.</summary>
    public static SiteBuilderObjectChange AddMultilanguagePage(this SiteBuilderChangePreview preview, MultilanguagePage from, MultilanguagePage to)
    {
        ArgumentNullException.ThrowIfNull(to);

        var change = preview.AddObject(ModuleConstants.ChangePreview.MultilanguagePage, from?.Id)
            .AddField(Name, from?.Name, to.Name)
            .AddField(StoreId, from?.StoreId, to.StoreId)
            .AddField(DefaultCultureName, from?.DefaultCultureName, to.DefaultCultureName)
            .AddField(Permalink, from?.Permalink, to.Permalink);

        return AddMultilanguagePageAudience(change, from, to);
    }

    private static void AddPageNaming(SiteBuilderObjectChange change, GroupedSiteBuilderPage from, GroupedSiteBuilderPage to) =>
        change
            .AddField(Status, from?.Status, from?.Status ?? Draft)
            .AddField(Name, from?.Name, to.Name)
            .AddField(StoreId, from?.StoreId, to.StoreId)
            .AddField(CultureName, from?.CultureName, to.CultureName)
            .AddField(Permalink, from?.Permalink, to.Permalink);

    private static void AddPageAudience(SiteBuilderObjectChange change, GroupedSiteBuilderPage from, GroupedSiteBuilderPage to) =>
        change
            .AddField(Visibility, from?.Visibility, to.Visibility)
            .AddField(UserGroups, from?.UserGroups, to.UserGroups)
            .AddField(OrganizationId, from?.OrganizationId, to.OrganizationId)
            .AddField(StartDate, from?.StartDate, to.StartDate)
            .AddField(EndDate, from?.EndDate, to.EndDate);

    private static void AddPageLanguage(SiteBuilderObjectChange change, GroupedSiteBuilderPage from, GroupedSiteBuilderPage to) =>
        change
            .AddField(MultilanguagePageId, from?.MultilanguagePageId, to.MultilanguagePageId)
            .AddField(OverriddenFields, from?.OverriddenFields, to.OverriddenFields ?? from?.OverriddenFields)
            .AddField(SourceRevisionDate, from?.SourceRevisionDate, to.SourceRevisionDate);

    private static SiteBuilderObjectChange AddMultilanguagePageAudience(SiteBuilderObjectChange change, MultilanguagePage from, MultilanguagePage to) =>
        change
            .AddField(Visibility, from?.Visibility, to.Visibility)
            .AddField(UserGroups, from?.UserGroups, to.UserGroups)
            .AddField(OrganizationId, from?.OrganizationId, to.OrganizationId)
            .AddField(StartDate, from?.StartDate, to.StartDate)
            .AddField(EndDate, from?.EndDate, to.EndDate);

    private static string Format(object value) => value switch
    {
        null => null,
        bool flag => flag ? "true" : "false",
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };
}
