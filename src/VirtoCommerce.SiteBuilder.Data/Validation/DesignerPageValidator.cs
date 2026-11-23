using FluentValidation;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Data.Validation;

public class DesignerPageValidator : AbstractValidator<DesignerSavePage>
{
    public DesignerPageValidator()
    {
        ClassLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => x)
            .Must(x => string.IsNullOrEmpty(x.GroupId) != string.IsNullOrEmpty(x.Path))
            .WithMessage("Name the page by its groupId (a database page) or by its path and type (a file page).");

        RuleFor(x => x.GroupId)
            .Must(SiteBuilderContentPaths.IsFolderName)
            .When(x => !string.IsNullOrEmpty(x.GroupId))
            .WithMessage("\"{PropertyValue}\" is not a page group id.");

        RuleFor(x => x.Type)
            .Must(GitPageLocation.IsPageContent)
            .When(x => !string.IsNullOrEmpty(x.Path))
            .WithMessage("A file page is a page or a blog article: a theme file goes with the theme files.");
    }
}
