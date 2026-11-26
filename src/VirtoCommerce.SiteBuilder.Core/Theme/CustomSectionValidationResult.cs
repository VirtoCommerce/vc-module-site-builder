namespace VirtoCommerce.SiteBuilder.Core.Theme;

public class CustomSectionValidationResult
{
    /// <summary>The file in the theme the errors and the warnings are of, which they do not repeat.</summary>
    public string File { get; set; }

    public IList<string> Errors { get; set; } = [];

    public IList<string> Warnings { get; set; } = [];
}
