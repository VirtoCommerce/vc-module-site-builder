namespace VirtoCommerce.SiteBuilder.Core.Services;

/// <summary>Site Builder's own HTML sanitizer, with the profile of the storefront's section sanitizer.</summary>
public interface ISiteBuilderHtmlSanitizer
{
    /// <summary>Idempotent: what it returns comes back unchanged. A text without markup comes back as it is.</summary>
    string Sanitize(string html);
}
