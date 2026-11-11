using Microsoft.AspNetCore.Authorization;
using VirtoCommerce.SiteBuilder.Core;

namespace VirtoCommerce.SiteBuilder.Data.Authorization
{
    /// <summary>Does not derive from <c>PermissionAuthorizationRequirement</c>: the platform's default handler grants those on a global permission alone, without the store check.</summary>
    public sealed class SiteBuilderAuthorizationRequirement : IAuthorizationRequirement
    {
        /// <summary>Reading the store's Site Builder content: the permission the store scope is assigned to.</summary>
        public SiteBuilderAuthorizationRequirement()
            : this(ModuleConstants.Security.Permissions.Read)
        {
        }

        public SiteBuilderAuthorizationRequirement(string permission)
        {
            Permission = permission;
        }

        public string Permission { get; }
    }
}
