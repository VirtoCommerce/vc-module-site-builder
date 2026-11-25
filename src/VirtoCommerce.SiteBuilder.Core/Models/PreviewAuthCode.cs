namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A one-time code the storefront exchanges at its own token endpoint for the preview user's access token.</summary>
public class PreviewAuthCode
{
    public string Code { get; set; }

    public int ExpiresInSeconds { get; set; }
}
