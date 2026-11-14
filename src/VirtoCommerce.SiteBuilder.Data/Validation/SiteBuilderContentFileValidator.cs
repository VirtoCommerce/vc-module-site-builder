using FluentValidation;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Data.Validation;

public class SiteBuilderContentFileValidator : AbstractValidator<SiteBuilderContentFile>
{
    public SiteBuilderContentFileValidator()
    {
        ClassLevelCascadeMode = CascadeMode.Stop;
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => x.ContentType)
            .Must(SiteBuilderContentPaths.IsContentType)
            .WithMessage("The content type of a file must be themes, pages or blogs.");

        RuleFor(x => x.Path)
            .ContentPath();

        When(x => SiteBuilderContentPaths.IsPageContentType(x.ContentType), () =>
        {
            RuleFor(x => x.Path)
                .PageDocument();

            RuleFor(x => x.Content)
                .Custom((content, context) =>
                {
                    foreach (var error in PageEnvelopeValidator.Validate(content))
                    {
                        context.AddFailure(nameof(SiteBuilderContentFile.Content), $"{context.InstanceToValidate.Path}: {error}");
                    }
                });
        }).Otherwise(() =>
        {
            RuleFor(x => x.Path)
                .Must(SiteBuilderContentPaths.IsJsonFile)
                .WithMessage("\"{PropertyValue}\" is not a theme JSON file.")
                .Must(SiteBuilderContentPaths.IsInsideAThemeFolder)
                .WithMessage("\"{PropertyValue}\" is not inside a theme folder.")
                .Must(SiteBuilderContentPaths.CanSaveThemeFile)
                .WithMessage("\"{PropertyValue}\" is in the theme's config folder, which is deployed with the theme: the builder saves only the theme settings, template rules, brand and palette there.");
        });
    }
}
