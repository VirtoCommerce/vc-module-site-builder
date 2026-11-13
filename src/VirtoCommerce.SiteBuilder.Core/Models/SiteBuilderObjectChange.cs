namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SiteBuilderObjectChange
{
    /// <summary>One of <see cref="ModuleConstants.ChangePreview"/>'s object types.</summary>
    public string ObjectType { get; set; }

    /// <summary>
    /// The group id of a page, the id of a multilanguage page; the path of a file page, or of a theme file with its theme folder.
    /// <c>null</c> for an object the write creates.
    /// </summary>
    public string ObjectId { get; set; }

    /// <summary>The version of what the store holds now, where it keeps one: a write sent at another version is refused.</summary>
    public string ETag { get; set; }

    public IList<SiteBuilderFieldChange> Fields { get; set; } = [];
}
