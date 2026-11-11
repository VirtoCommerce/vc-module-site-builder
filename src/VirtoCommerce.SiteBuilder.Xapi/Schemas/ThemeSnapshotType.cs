using GraphQL.Types;
using VirtoCommerce.SiteBuilder.Xapi.Models;
using VirtoCommerce.Xapi.Core.Schemas;
using VirtoCommerce.XCMS.Core.Schemas;

namespace VirtoCommerce.SiteBuilder.Xapi.Schemas;

public class ThemeSnapshotType : ExtendableGraphType<ThemeSnapshotResult>
{
    public ThemeSnapshotType()
    {
        Name = "ThemeSnapshot";

        Field(x => x.StoreId, nullable: false).Description("The store the snapshot belongs to");
        Field(x => x.CultureName, nullable: false).Description("The language the texts are in");
        Field(x => x.Publication, nullable: false).Description("The number of the theme publication");
        Field(x => x.Content, nullable: false).Description("The snapshot JSON, as Page Builder published it");
        ExtendableField<NonNullGraphType<ListGraphType<NonNullGraphType<MenuLinkListType>>>>(
            "menus",
            "The link lists the header and footer name, under the names they hold",
            resolve: context => context.Source.Menus);
        ExtendableField<ThemeSnapshotSlugType>(
            "slug",
            "What is at the permalink asked for, for an anonymous visitor; null without a permalink, with nothing at it or when the lookup failed",
            resolve: context => context.Source.Slug);
    }
}
