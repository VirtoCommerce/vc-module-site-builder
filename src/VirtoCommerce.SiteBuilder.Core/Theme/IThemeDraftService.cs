namespace VirtoCommerce.SiteBuilder.Core.Theme;

public interface IThemeDraftService
{
    /// <summary>Reads the file as the designer edits it: its draft, else what is live. The path has the theme folder.</summary>
    Task<ThemeFileContent> ReadAsync(string storeId, string path, CancellationToken cancellationToken = default);

    /// <summary>Under the store's theme lock, writes the drafts and runs <paramref name="saveOthersAsync"/>; when either fails, puts the drafts back. Files are keyed by their path with the theme folder.</summary>
    /// <param name="eTags">The version each file was read at, by its path; null writes the files whatever they hold.</param>
    /// <exception cref="Models.SiteBuilderContentConflictException">A file was saved by someone else since it was read: nothing is written.</exception>
    Task<T> SaveDraftsAsync<T>(string storeId, IDictionary<string, string> files, IDictionary<string, string> eTags,
        Func<CancellationToken, Task<T>> saveOthersAsync, CancellationToken cancellationToken = default);

    /// <summary>Under the store's theme lock, deletes the drafts of the published files and runs <paramref name="discardOthersAsync"/>; when it fails, puts the drafts back.</summary>
    Task<IList<DiscardedThemeDraft>> DiscardDraftsAsync(string storeId, IList<string> paths, Func<CancellationToken, Task> discardOthersAsync,
        CancellationToken cancellationToken = default);

    /// <summary>What <see cref="DiscardDraftsAsync"/> would do with the draft of the file, with the draft and what is live; writes nothing.</summary>
    Task<Models.DraftDiscardPreview> PreviewDiscardAsync(string storeId, string path, CancellationToken cancellationToken = default);
}
