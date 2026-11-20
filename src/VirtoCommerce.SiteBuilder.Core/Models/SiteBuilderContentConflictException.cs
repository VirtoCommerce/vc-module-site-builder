namespace VirtoCommerce.SiteBuilder.Core.Models;

public sealed class SiteBuilderContentConflictException(string message) : Exception(message)
{
    public const string ThemeFileMessage = "A theme file was saved by someone else since it was read.";

    public const string ThemeDraftsMessage = "The theme's drafts changed since they were reviewed: review them again before publishing.";

    public const string PageChangedMessage = "The page changed since it was reviewed: review it again.";

    public const string DraftChangedMessage = "The draft changed since it was reviewed: review it again.";

    public const string DraftBranchMovedMessage = "The draft branch moved since it was reviewed: review it again.";

    public const string SettingsChangedMessage = "The page settings changed since they were reviewed: review them again.";

    public const string PublishedPageMessage = "The change would reach a published page, which the request does not allow: nothing was changed.";

    public const string SourceRevisionChangedMessage = "The source language version was published again since it was reviewed: review it again.";

    public SiteBuilderContentConflictException() : this("The page content version does not match the current version.")
    {
    }
}
