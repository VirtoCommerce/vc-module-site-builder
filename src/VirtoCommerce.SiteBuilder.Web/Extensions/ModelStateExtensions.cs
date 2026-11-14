using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace VirtoCommerce.SiteBuilder.Web.Extensions;

public static class ModelStateExtensions
{
    /// <summary>What MVC could not bind, where it would pass the parameter's default on.</summary>
    public static IList<string> GetBindingErrors(this ModelStateDictionary modelState) =>
        modelState?.Values
            .SelectMany(entry => entry.Errors)
            .Select(error => string.IsNullOrEmpty(error.ErrorMessage) ? "The request could not be read." : error.ErrorMessage)
            .ToList() ?? [];
}
