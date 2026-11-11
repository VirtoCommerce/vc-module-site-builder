namespace VirtoCommerce.SiteBuilder.Core.Services;

public interface ISiteBuilderSharedComponentResolver
{
    Task<string> ResolveAsync(string content, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, string>> LoadReferencedComponentsAsync(
        IEnumerable<string> pageContents,
        CancellationToken cancellationToken = default);
    string Expand(string content, IReadOnlyDictionary<string, string> componentContents);
}
