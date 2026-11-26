using System.Linq;
using FluentValidation;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Theme;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Services;

namespace VirtoCommerce.SiteBuilder.Tests;

internal static class RequestValidators
{
    public static IValidator<GitHubAppSetupRequest> AppSetup { get; } = new GitHubAppSetupRequestValidator();

    public static IValidator<SiteBuilderStoreRequest> Stores(params string[] storeIds) =>
        new SiteBuilderStoreRequestValidator(StoreService(storeIds));

    public static IValidator<ContentHistoryRequest> History(params string[] storeIds) =>
        new ContentHistoryRequestValidator(Stores(storeIds));

    public static IValidator<ContentHistoryRestoreRequest> Restore(params string[] storeIds) =>
        new ContentHistoryRestoreRequestValidator(History(storeIds), Themes(storeIds));

    public static IValidator<SiteBuilderContentRequest> Content(params string[] storeIds) =>
        Content(StoreService(storeIds));

    public static IValidator<SiteBuilderContentRequest> Content(IStoreService storeService) =>
        new SiteBuilderContentRequestValidator(new SiteBuilderStoreRequestValidator(storeService), new SiteBuilderContentFileValidator(),
            new ThemeStorageFactory(storeService, null, null, null));

    public static IValidator<DesignerSaveRequest> DesignerSave { get; } = new DesignerSaveRequestValidator(new DesignerPageValidator());

    public static IValidator<DesignerDiscardRequest> DesignerDiscard { get; } = new DesignerDiscardRequestValidator(new DesignerPageValidator());

    private static ThemeStorageFactory Themes(params string[] storeIds) => new(StoreService(storeIds), null, null, null);

    private static FakeStoreService StoreService(params string[] storeIds) => new(storeIds.Select(id => new Store { Id = id, Name = id }).ToArray());
}
