namespace VirtoCommerce.SiteBuilder.Core.Models;

public sealed class SiteBuilderContentConflictException(string message) : Exception(message)
{
    public const string ThemeFileMessage = "A theme file was saved by someone else since it was read.";

    public const string ThemeDraftsMessage = "The theme's drafts changed since they were reviewed: review them again before publishing.";

    public SiteBuilderContentConflictException() : this("The page content version does not match the current version.")
    {
    }
}
