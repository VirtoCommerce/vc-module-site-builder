using FluentValidation;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Data.Validation;

public class SiteBuilderContentRequestValidator : AbstractValidator<SiteBuilderContentRequest>
{
    // GitHub's GraphQL connections return at most this many nodes.
    private const int MaxHistoryTake = 100;

    public SiteBuilderContentRequestValidator(IValidator<SiteBuilderStoreRequest> storeRequestValidator, IValidator<SiteBuilderContentFile> fileValidator)
    {
        ClassLevelCascadeMode = CascadeMode.Stop;
        RuleLevelCascadeMode = CascadeMode.Stop;

        // A save has no type of its own: each file names one.
        RuleFor(x => x.ContentType)
            .Must(SiteBuilderContentPaths.IsContentType)
            .Unless(x => x.ContentType == null && x.Files != null)
            .WithMessage("The content type must be themes, pages or blogs.");

        RuleFor(x => x.ContentType)
            .Must(SiteBuilderContentPaths.IsPageContentType)
            .When(x => x.Pages is { Count: > 0 } || x.LegacyDrafts is { Count: > 0 })
            .WithMessage("Only pages and blog articles are addressed here: the content type must be pages or blogs.");

        RuleFor(x => x.Theme)
            .Must(SiteBuilderContentPaths.IsFolderName)
            .When(x => !string.IsNullOrEmpty(x.Theme))
            .WithMessage("Theme \"{PropertyValue}\" must be one folder name.");

        RuleForEach(x => x.Paths)
            .ContentPath();

        RuleForEach(x => x.Pages)
            .ContentPath()
            .PageDocument();

        RuleForEach(x => x.LegacyDrafts)
            .ContentPath()
            .Must(SiteBuilderContentPaths.IsLegacyDraft)
            .WithMessage("Only \"-draft\" files can be deleted here, by their exact path: \"{PropertyValue}\" is not one.");

        RuleForEach(x => x.Files)
            .NotNull()
            .WithMessage("The list of files has an empty entry.")
            .SetValidator(fileValidator);

        RuleFor(x => x.Ref)
            .Must(GitCommitSha.IsSha)
            .When(x => x.Ref != null)
            .WithMessage("\"{PropertyValue}\" is not a commit sha.");

        RuleFor(x => x.Keyword)
            .Must(SiteBuilderContentPaths.IsSearchKeyword)
            .When(x => !string.IsNullOrEmpty(x.Keyword))
            .WithMessage("Keyword \"{PropertyValue}\" may not contain \\ / : * ? \" < > | or a control character.");

        RuleFor(x => x.HistoryTake)
            .InclusiveBetween(1, MaxHistoryTake)
            .When(x => x.HistoryTake.HasValue)
            .WithMessage($"take must be between 1 and {MaxHistoryTake}.");

        RuleFor(x => x.HistoryAfter)
            .Must(SiteBuilderContentPaths.IsHistoryCursor)
            .When(x => x.HistoryAfter != null)
            .WithMessage("\"{PropertyValue}\" is not a history cursor.");

        RuleFor(x => x.SchemaKind)
            .Must(SiteBuilderContentPaths.IsSchemaKind)
            .When(x => x.SchemaKind != null || x.SchemaKey != null)
            .WithMessage(x => $"Unknown kind '{x.SchemaKind}'. Expected one of: {string.Join(", ", ModuleConstants.SchemaKinds.All)}.");

        RuleFor(x => x.SchemaKey)
            .Must(SiteBuilderContentPaths.IsFolderName)
            .When(x => x.SchemaKind != null || x.SchemaKey != null)
            .WithMessage("\"{PropertyValue}\" is not a schema name.");

        Include(storeRequestValidator);
    }
}
