using System;
using System.Collections.Generic;

namespace VirtoCommerce.SiteBuilder.Data.ExportImport;

public class SiteBuilderExportSharedComponent
{
    public string Id { get; set; }
    public string StoreId { get; set; }
    public string Name { get; set; }
    public string Content { get; set; }
}

public class SiteBuilderExportPage
{
    public string GroupId { get; set; }
    public string Name { get; set; }
    public string Permalink { get; set; }
    public string StoreId { get; set; }
    public string CultureName { get; set; }
    public bool Visibility { get; set; }
    public string UserGroups { get; set; }
    public string OrganizationId { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    /// <summary>The multilanguage page this page is a language version of; exported in SiteBuilderMultilanguagePages.</summary>
    public string MultilanguagePageId { get; set; }
    public IList<SiteBuilderExportPageVariant> Variants { get; set; } = [];
}

public class SiteBuilderExportPageVariant
{
    public string PageId { get; set; }
    public string Status { get; set; }
    public string Content { get; set; }
}
