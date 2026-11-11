using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.SiteBuilder.Web.Controllers.Api;
using Xunit;
using static VirtoCommerce.SiteBuilder.Core.ModuleConstants.Security;

namespace VirtoCommerce.SiteBuilder.Tests
{
    /// <summary>
    /// MVC checks [Authorize] before the action runs, so a test calling the action never sees it: the attributes are asserted here.
    /// </summary>
    public class SiteBuilderControllerPermissionsTests
    {
        // builder:read can be scoped to stores, so it has no policy here and is checked in the action with the store.
        private static readonly Dictionary<string, string[]> Expected = new()
        {
            { nameof(SiteBuilderController.GetTemplate), [] },
            { nameof(SiteBuilderController.GetSettings), [] },
            { nameof(SiteBuilderController.GetTemplates), [] },
            { nameof(SiteBuilderController.GetObjects), [] },
            { nameof(SiteBuilderController.GetSectionsSettings), [] },
            { nameof(SiteBuilderController.GetSchemasCatalog), [] },
            { nameof(SiteBuilderController.GetSchemaByKey), [] },
            { nameof(SiteBuilderController.Search), [] },
            { nameof(SiteBuilderController.SaveTemplates), [Permissions.Update] },
            { nameof(SiteBuilderController.GetDesignerThemeFile), [] },
            { nameof(SiteBuilderController.GetDesignerStorefront), [] },
            { nameof(SiteBuilderController.SaveDesignerChanges), [Permissions.Update] },
            { nameof(SiteBuilderController.DiscardDesignerChanges), [Permissions.Update] },
            { nameof(SiteBuilderController.GitPublish), [Permissions.Publish] },
            { nameof(SiteBuilderController.GitPromote), [Permissions.Promote] },
            { nameof(SiteBuilderController.GitUnpublish), [Permissions.Publish] },
            { nameof(SiteBuilderController.GitPublishStatus), [] },
            { nameof(SiteBuilderController.GitHistory), [] },
            { nameof(SiteBuilderController.GitRestoreVersion), [Permissions.Update] },
            { nameof(SiteBuilderController.GitPreview), [] },
            { nameof(SiteBuilderController.DiscardDraft), [Permissions.Update] },
            { nameof(SiteBuilderController.GitDiscardDraft), [Permissions.Update] },
            { nameof(SiteBuilderController.GetLegacyDrafts), [] },
            { nameof(SiteBuilderController.DeleteLegacyDrafts), [Permissions.Delete] },
            { nameof(SiteBuilderController.GitCopyPage), [Permissions.Create] },
            { nameof(SiteBuilderController.GetLegacyDraft), [] },
        };

        public static TheoryData<string, string[]> ActionPermissions =>
            new(Expected.Select(row => new TheoryDataRow<string, string[]>(row.Key, row.Value)));

        [Theory]
        [MemberData(nameof(ActionPermissions))]
        public void Action_RequiresItsPermissions(string action, string[] permissions)
        {
            var method = typeof(SiteBuilderController).GetMethod(action);

            Assert.NotNull(method);
            Assert.Equal(permissions.Order(), PoliciesOf(method).Order());
        }

        [Fact]
        public void EveryAction_IsListedAbove()
        {
            Assert.All(Actions().Select(method => method.Name), action => Assert.Contains(action, Expected.Keys));
        }

        [Fact]
        public void NoAction_IsOpenToAnonymousCallers()
        {
            Assert.True(typeof(SiteBuilderController).IsDefined(typeof(AuthorizeAttribute), inherit: true));
            Assert.Empty(typeof(SiteBuilderController).GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>());
            Assert.All(Actions(), action => Assert.Empty(action.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>()));
        }

        // Everything MVC routes, HTTP method attribute or not: a public method without a route of its own takes the controller's.
        private static IEnumerable<MethodInfo> Actions() =>
            typeof(SiteBuilderController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName && !method.IsDefined(typeof(NonActionAttribute), inherit: true));

        private static string[] PoliciesOf(MethodInfo method) =>
            method.GetCustomAttributes<AuthorizeAttribute>().Select(attribute => attribute.Policy).Where(policy => policy != null).ToArray();
    }
}
