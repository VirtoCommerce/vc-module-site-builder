using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.Platform.Core;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Security;

namespace VirtoCommerce.SiteBuilder.Data.Authorization
{
    public sealed class SiteBuilderAuthorizationHandler(Func<UserManager<ApplicationUser>> userManagerFactory)
        : AuthorizationHandler<SiteBuilderAuthorizationRequirement>
    {
        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, SiteBuilderAuthorizationRequirement requirement)
        {
            var result = context.User.IsInRole(PlatformConstants.Security.SystemRoles.Administrator);

            if (!result)
            {
                var user = await GetUser(context);
                if (user == null)
                {
                    context.Fail();
                }

                switch (context.Resource)
                {
                    case IHasStoreId hasStoreId:
                        result = hasStoreId.StoreId.EqualsIgnoreCase(user?.StoreId);
                        break;
                    case IEnumerable<IHasStoreId> hasStoreIds:
                        result = hasStoreIds.All(x => x.StoreId.EqualsIgnoreCase(user?.StoreId));
                        break;
                }
            }

            if (result)
            {
                context.Succeed(requirement);
            }
            else
            {
                context.Fail();
            }
        }

        private async Task<ApplicationUser> GetUser(AuthorizationHandlerContext context)
        {
            var userId = context.User.GetUserId();

            using var userManager = userManagerFactory();
            var user = await userManager.FindByIdAsync(userId);
            return user;
        }
    }
}

