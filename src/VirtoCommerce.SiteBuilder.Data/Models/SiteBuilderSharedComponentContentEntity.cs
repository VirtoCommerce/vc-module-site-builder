using System.ComponentModel.DataAnnotations;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Data.Models;

public class SiteBuilderSharedComponentContentEntity : Entity
{
    [Required]
    public string ComponentContent { get; set; }

    public virtual SiteBuilderSharedComponentEntity Component { get; set; }
}
