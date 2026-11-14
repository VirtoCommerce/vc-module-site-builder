using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Security;

namespace VirtoCommerce.SiteBuilder.Data.Authorization
{
    public sealed class SiteBuilderAuthorizationHandler(Func<UserManager<ApplicationUser>> userManagerFactory, IOptions<MvcNewtonsoftJsonOptions> jsonOptions)
        : AuthorizationHandler<SiteBuilderAuthorizationRequirement>
    {
        // A refusal stays silent: failing here would override any other handler registered for this requirement.
        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, SiteBuilderAuthorizationRequirement requirement)
        {
            if (context.User.IsInRole(PlatformConstants.Security.SystemRoles.Administrator))
            {
                context.Succeed(requirement);
                return;
            }

            var storeIds = GetStoreIds(context.Resource);
            var user = await GetUser(context);
            if (user == null || IsBoundToAnotherStore(user, storeIds))
            {
                return;
            }

            if (context.User.HasGlobalPermission(requirement.Permission))
            {
                context.Succeed(requirement);
                return;
            }

            if (storeIds == null)
            {
                return;
            }

            if (HasPermissionInStores(context.User, requirement.Permission, storeIds))
            {
                context.Succeed(requirement);
            }
        }

        // an account bound to a store works in that store only
        private static bool IsBoundToAnotherStore(ApplicationUser user, IList<string> storeIds) =>
            !string.IsNullOrEmpty(user.StoreId) && storeIds?.All(x => x.EqualsIgnoreCase(user.StoreId)) != true;

        private bool HasPermissionInStores(ClaimsPrincipal principal, string permission, IList<string> storeIds)
        {
            var scopedStoreIds = principal.FindPermissions(permission, jsonOptions.Value.SerializerSettings)
                .SelectMany(x => x.AssignedScopes.OfType<StoreSelectedScope>())
                .Select(x => x.StoreId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return scopedStoreIds.Count > 0 && storeIds.All(x => x != null && scopedStoreIds.Contains(x));
        }

        private static IList<string> GetStoreIds(object resource) => resource switch
        {
            Store store => [store.Id],
            IHasStoreId hasStoreId => [hasStoreId.StoreId],
            IEnumerable<IHasStoreId> hasStoreIds => hasStoreIds.Select(x => x.StoreId).ToList(),
            _ => null,
        };

        private async Task<ApplicationUser> GetUser(AuthorizationHandlerContext context)
        {
            var userId = context.User.GetUserId();

            using var userManager = userManagerFactory();
            var user = await userManager.FindByIdAsync(userId);
            return user;
        }
    }
}
