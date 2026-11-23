using FluentValidation;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.StoreModule.Core.Services;

namespace VirtoCommerce.SiteBuilder.Data.Validation;

public class SiteBuilderStoreRequestValidator : AbstractValidator<SiteBuilderStoreRequest>
{
    public SiteBuilderStoreRequestValidator(IStoreService storeService)
    {
        ClassLevelCascadeMode = CascadeMode.Stop;
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => x.CatalogHash)
            .Must(SiteBuilderContentPaths.IsCatalogHash)
            .When(x => x.CatalogHash != null)
            .WithMessage("\"{PropertyValue}\" is not a catalog hash.");

        RuleFor(x => x.StoreId)
            .NotEmpty()
            .MustAsync(async (storeId, _) => await storeService.GetNoCloneAsync(storeId) != null)
            .WithMessage("Store \"{PropertyValue}\" does not exist.");
    }
}
