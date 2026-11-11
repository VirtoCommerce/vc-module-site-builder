using System.ComponentModel.DataAnnotations;
using VirtoCommerce.Platform.Core.Common;
using static VirtoCommerce.Platform.Data.Infrastructure.DbContextBase;

namespace VirtoCommerce.SiteBuilder.Data.Models;

public class SiteBuilderSharedComponentReferenceEntity : Entity
{
    [StringLength(IdLength)]
    [Required]
    public string PageId { get; set; }

    [StringLength(IdLength)]
    [Required]
    public string SharedComponentId { get; set; }
}
