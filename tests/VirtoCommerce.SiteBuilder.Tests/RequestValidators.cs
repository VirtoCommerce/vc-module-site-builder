using System.Linq;
using FluentValidation;
using VirtoCommerce.SiteBuilder.Core.GitContent;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Data.Validation;
using VirtoCommerce.StoreModule.Core.Model;

namespace VirtoCommerce.SiteBuilder.Tests;

internal static class RequestValidators
{
    public static IValidator<GitHubAppSetupRequest> AppSetup { get; } = new GitHubAppSetupRequestValidator();

    public static IValidator<SiteBuilderStoreRequest> Stores(params string[] storeIds) =>
        new SiteBuilderStoreRequestValidator(new FakeStoreService(storeIds.Select(id => new Store { Id = id, Name = id }).ToArray()));

    public static IValidator<ContentHistoryRequest> History(params string[] storeIds) =>
        new ContentHistoryRequestValidator(Stores(storeIds));

    public static IValidator<ContentHistoryRestoreRequest> Restore(params string[] storeIds) =>
        new ContentHistoryRestoreRequestValidator(History(storeIds));

    public static IValidator<SiteBuilderContentRequest> Content(params string[] storeIds) =>
        new SiteBuilderContentRequestValidator(Stores(storeIds), new SiteBuilderContentFileValidator());

    public static IValidator<DesignerSaveRequest> DesignerSave { get; } = new DesignerSaveRequestValidator(new DesignerPageValidator());

    public static IValidator<DesignerDiscardRequest> DesignerDiscard { get; } = new DesignerDiscardRequestValidator(new DesignerPageValidator());
}
