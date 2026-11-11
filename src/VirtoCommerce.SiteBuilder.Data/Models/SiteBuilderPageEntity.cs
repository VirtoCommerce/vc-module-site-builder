using System.ComponentModel.DataAnnotations;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Domain;
using static VirtoCommerce.Platform.Data.Infrastructure.DbContextBase;

namespace VirtoCommerce.SiteBuilder.Data.Models;

public class SiteBuilderPageEntity : AuditableEntity, IDataEntity<SiteBuilderPageEntity, SiteBuilderPage>
{
    [StringLength(IdLength)]
    public string GroupId { get; set; }

    [StringLength(IdLength)]
    public string StoreId { get; set; }

    [StringLength(128)]
    public string Status { get; set; } // Draft | Published | Archived

    public virtual SiteBuilderContentEntity Content { get; set; }

    public GroupedSiteBuilderPageEntity Group { get; set; }

    public virtual SiteBuilderPage ToModel(SiteBuilderPage model)
    {
        model.Id = Id;
        model.CreatedBy = CreatedBy;
        model.CreatedDate = CreatedDate;
        model.ModifiedBy = ModifiedBy;
        model.ModifiedDate = ModifiedDate;

        model.GroupId = GroupId;
        model.StoreId = StoreId;
        model.Status = Status;

        return model;
    }

    public virtual SiteBuilderPageEntity FromModel(SiteBuilderPage model, PrimaryKeyResolvingMap pkMap)
    {
        pkMap.AddPair(model, this);

        Id = model.Id;
        CreatedBy = model.CreatedBy;
        CreatedDate = model.CreatedDate;
        ModifiedBy = model.ModifiedBy;
        ModifiedDate = model.ModifiedDate;

        GroupId = model.GroupId;
        StoreId = model.StoreId;
        Status = model.Status;

        if (model.Content != null)
        {
            Content = new SiteBuilderContentEntity { PageContent = model.Content };
        }

        return this;
    }

    public virtual void Patch(SiteBuilderPageEntity target)
    {
        target.GroupId = GroupId;
        target.StoreId = StoreId;
        target.Status = Status;
        if (Content != null)
        {
            target.Content ??= new SiteBuilderContentEntity();
            target.Content.PageContent = Content.PageContent;
        }
    }
}
