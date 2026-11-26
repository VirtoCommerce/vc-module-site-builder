using FluentValidation;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Theme;

namespace VirtoCommerce.SiteBuilder.Data.Validation;

/// <summary>Shared, so that a path is checked the same way wherever a request names one.</summary>
public static class SiteBuilderContentRules
{
    private const string EnvelopeError = nameof(EnvelopeError);

    public static IRuleBuilderOptions<T, string> ContentPath<T>(this IRuleBuilder<T, string> rule) =>
        rule
            .NotEmpty()
            .WithMessage("A path is required.")
            .Must(SiteBuilderContentPaths.HasPathCharactersOnly)
            .WithMessage("Path \"{PropertyValue}\" contains \"\\\", \":\" or a control character.")
            .Must(SiteBuilderContentPaths.StaysInsideTheFolder)
            .WithMessage("Path \"{PropertyValue}\" must stay inside the store's content folder.")
            .Must(SiteBuilderContentPaths.HasPortableNames)
            .WithMessage("Path \"{PropertyValue}\" has a name ending in a dot or a space, which Windows storage reads as another name.");

    public static IRuleBuilderOptions<T, string> PageDocument<T>(this IRuleBuilder<T, string> rule) =>
        rule
            .Must(SiteBuilderContentPaths.IsPageDocument)
            .WithMessage("\"{PropertyValue}\" is not a page document: pages and blog articles are .page or .page-draft files.");

    public static IRuleBuilderOptions<T, string> PageEnvelope<T>(this IRuleBuilder<T, string> rule) =>
        rule
            .NotEmpty()
            .WithMessage("Content is required.")
            .Must((_, content, context) =>
            {
                if (string.IsNullOrWhiteSpace(content) || PageContentEnvelope.TryValidate(content, out var error))
                {
                    return true;
                }

                context.MessageFormatter.AppendArgument(EnvelopeError, error);
                return false;
            })
            .WithMessage($"{{{EnvelopeError}}}");

    /// <summary>
    /// A theme file is read and written in the theme folder its path starts with, and only the store's theme is published: a path in
    /// another folder is refused rather than written or deleted where nothing reads it. The folder is compared as blob storage and git name it:
    /// in another case it is another folder.
    /// </summary>
    /// <param name="getThemeFiles">The paths, with the theme folder, of the theme files the value names.</param>
    public static IRuleBuilderOptionsConditions<T, TProperty> InTheStoresTheme<T, TProperty>(this IRuleBuilder<T, TProperty> rule,
        IThemeStorageFactory themeStorageFactory, Func<TProperty, IEnumerable<string>> getThemeFiles)
        where T : SiteBuilderStoreRequest =>
        rule.CustomAsync(async (value, context, _) =>
        {
            var paths = value == null ? [] : getThemeFiles(value).ToList();
            if (paths.Count == 0)
            {
                return;
            }

            var themeName = await themeStorageFactory.GetThemeNameAsync(context.InstanceToValidate.StoreId);
            foreach (var path in paths.Where(x => !string.Equals(SiteBuilderContentPaths.GetThemeName(x), themeName, StringComparison.Ordinal)))
            {
                context.AddFailure($"\"{path}\" is not in the store's theme folder \"{themeName}\".");
            }
        });

    public static IRuleBuilderOptions<T, string> ReadVersion<T>(this IRuleBuilder<T, string> rule, bool trim = false) =>
        rule
            .Must(eTag => string.IsNullOrWhiteSpace(eTag) || SiteBuilderContentVersion.IsWellFormed(trim ? eTag.Trim() : eTag))
            .WithMessage("The ETag must be the single strong ETag its read returned.");
}
