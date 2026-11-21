namespace VirtoCommerce.SiteBuilder.Core.Theme;

public class CustomSectionValidationResult
{
    /// <summary>One per rule the type breaks; any refuses it.</summary>
    public IList<string> Errors { get; set; } = [];

    /// <summary>What is saved as it is but the author should know, such as text the template holds in one language only.</summary>
    public IList<string> Warnings { get; set; } = [];
}
