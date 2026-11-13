using System.Security.Claims;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Theme;

namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>
/// Reads of a store's theme and content files for the designer: the storefront's schemas, the builder's settings, the theme's templates
/// and the files themselves. Each checks store access before the request, so a refusal does not tell which stores exist.
/// </summary>
public interface ISiteBuilderContentQueryService
{
    /// <summary>The schemas of a kind as one JSON object, by their key, as the theme stores them.</summary>
    Task<SiteBuilderOperationResult<string>> GetSchemasAsync(SiteBuilderSchemaQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>The schemas of sections, blocks, objects and shared fields as one JSON object, by their kind.</summary>
    Task<SiteBuilderOperationResult<string>> GetSectionSchemasAsync(SiteBuilderThemeQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The JSON catalog of the schemas: the key, name and a short description of each, by kind. A theme's internal schemas and its static
    /// sections are left out; the full schema is read with <see cref="GetSchemaAsync"/>.
    /// </summary>
    Task<SiteBuilderOperationResult<string>> GetSchemaCatalogAsync(SiteBuilderThemeQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>The JSON schema of one key. A template schema carries the fields of the theme's static sections too.</summary>
    Task<SiteBuilderOperationResult<string>> GetSchemaAsync(SiteBuilderSchemaQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>The builder's settings of the theme; for a store on the git flow, with where to publish, preview and restore pages.</summary>
    Task<SiteBuilderOperationResult<SiteBuilderFileContent>> GetBuilderSettingsAsync(SiteBuilderThemeQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A page, a blog article or a theme file. A page kept in git is read from git, at the version asked for or as the user's draft, else
    /// what is published; a page git does not hold yet is read from blob storage. Not found tells where the file was looked for.
    /// </summary>
    Task<SiteBuilderOperationResult<SiteBuilderFileContent>> ReadFileAsync(SiteBuilderFileReadRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);

    /// <summary>The templates of the theme, the default one of each type and the alternate ones, with whether each has a draft.</summary>
    Task<SiteBuilderOperationResult<IList<ThemeTemplate>>> ListTemplatesAsync(SiteBuilderThemeQueryRequest request, ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
