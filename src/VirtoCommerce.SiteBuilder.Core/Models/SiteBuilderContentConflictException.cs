namespace VirtoCommerce.SiteBuilder.Core.Models;

public sealed class SiteBuilderContentConflictException() : Exception("The page content version does not match the current version.");
