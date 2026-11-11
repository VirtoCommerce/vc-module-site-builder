using System;
using Microsoft.AspNetCore.Http;
using VirtoCommerce.SiteBuilder.Core;
using VirtoCommerce.SiteBuilder.Data.Validation;

namespace VirtoCommerce.SiteBuilder.Web.Extensions;

public static class HttpRequestExtensions
{
    /// <summary>Returns <c>null</c> when the request sends none or a malformed one.</summary>
    public static string GetSaveId(this HttpRequest request)
    {
        var value = request?.Headers[ModuleConstants.Headers.SaveId].ToString();
        return SiteBuilderContentPaths.IsSaveId(value) ? value : null;
    }

    public static string GetOrCreateSaveId(this HttpRequest request) => request.GetSaveId() ?? Guid.NewGuid().ToString("N");
}
