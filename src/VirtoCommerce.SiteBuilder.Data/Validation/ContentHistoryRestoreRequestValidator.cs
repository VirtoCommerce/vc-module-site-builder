using FluentValidation;
using VirtoCommerce.SiteBuilder.Core.History;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Theme;

namespace VirtoCommerce.SiteBuilder.Data.Validation;

public class ContentHistoryRestoreRequestValidator : AbstractValidator<ContentHistoryRestoreRequest>
{
    public ContentHistoryRestoreRequestValidator(IValidator<ContentHistoryRequest> requestValidator, IThemeStorageFactory themeStorageFactory)
    {
        ClassLevelCascadeMode = CascadeMode.Stop;

        Include(requestValidator);

        RuleFor(x => x.Items)
            .Must(items => DesignerThemeFiles.AreNamedOnce(items.Select(GetThemeFile).Where(x => x != null).ToList()))
            .WithMessage(DesignerThemeFiles.NamingError);

        RuleFor(x => x.Items)
            .InTheStoresTheme(themeStorageFactory, items => items.Select(GetThemeFile).Where(x => x != null));

        RuleFor(x => x.Items)
            .SavedByTheBuilder(items => items.Select(GetThemeFile).Where(x => x != null));
    }

    private static string GetThemeFile(string value) => HistoryItem.TryParse(value, out var item) && item.Type == HistoryItemType.Theme ? item.Key : null;
}
