namespace VirtoCommerce.SiteBuilder.Core.Models;

public class ThemeFilesValidationResult
{
    /// <summary>By the path with the theme folder: what each file is saved with that its author should know.</summary>
    public IDictionary<string, IList<string>> Warnings { get; set; } = new Dictionary<string, IList<string>>(StringComparer.OrdinalIgnoreCase);
}
