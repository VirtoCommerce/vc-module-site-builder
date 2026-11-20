using FluentValidation;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;

namespace VirtoCommerce.SiteBuilder.Data.Validation;

public class ContentHistoryRequestValidator : AbstractValidator<ContentHistoryRequest>
{
    // what one designer Save writes, with room for agents
    private const int MaxItems = 20;

    // GitHub's GraphQL connections return at most this many nodes.
    private const int MaxTake = 100;

    public ContentHistoryRequestValidator(IValidator<SiteBuilderStoreRequest> storeRequestValidator)
    {
        ClassLevelCascadeMode = CascadeMode.Stop;
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => x.Items)
            .NotEmpty()
            .WithMessage("Name at least one item: page:{groupId} or theme:{path}.")
            .Must(x => x.Count <= MaxItems)
            .WithMessage($"Name at most {MaxItems} items.");

        RuleForEach(x => x.Items)
            .Must(IsHistoryItem)
            .WithMessage("\"{PropertyValue}\" is not page:{groupId} or theme:{path} of a theme file outside snapshots/.");

        RuleFor(x => x.Items)
            .Must(x => x.Count(item => item.StartsWith(HistoryItem.PagePrefix, StringComparison.Ordinal)) <= 1)
            .When(x => x.VersionId != null)
            .WithMessage("A version is read or restored for one page at a time.");

        RuleFor(x => x.Take)
            .InclusiveBetween(1, MaxTake)
            .When(x => x.Take.HasValue)
            .WithMessage($"take must be between 1 and {MaxTake}.");

        RuleFor(x => x.VersionId)
            .Must(SiteBuilderContentPaths.IsHistoryVersionId)
            .When(x => x.VersionId != null)
            .WithMessage("\"{PropertyValue}\" is not a version id.");

        RuleFor(x => x.Cursor)
            .Must(SiteBuilderContentPaths.IsContentHistoryCursor)
            .When(x => x.Cursor != null)
            .WithMessage("\"{PropertyValue}\" is not a history cursor.");

        Include(storeRequestValidator);
    }

    private static bool IsHistoryItem(string value) =>
        HistoryItem.TryParse(value, out var item) && item.Type switch
        {
            HistoryItemType.Page => SiteBuilderContentPaths.IsFolderName(item.Key),
            HistoryItemType.Theme => SiteBuilderContentPaths.IsHistoryThemeFile(item.Key),
            _ => false,
        };
}
