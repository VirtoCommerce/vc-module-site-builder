using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class FileEntity : Entity
{
    public string Path { get; set; }

    public string Type { get; set; }
}
