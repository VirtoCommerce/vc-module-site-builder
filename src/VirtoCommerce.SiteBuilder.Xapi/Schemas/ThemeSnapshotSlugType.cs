using VirtoCommerce.SiteBuilder.Xapi.Models;
using VirtoCommerce.Xapi.Core.Schemas;

namespace VirtoCommerce.SiteBuilder.Xapi.Schemas;

public class ThemeSnapshotSlugType : ExtendableGraphType<ThemeSnapshotSlug>
{
    public ThemeSnapshotSlugType()
    {
        Name = "ThemeSnapshotSlug";

        Field(x => x.ObjectType, nullable: false).Description("The slug's object type: Pages, ContentFile, CatalogProduct, Category…");
        Field(x => x.ObjectId, nullable: false).Description("The id of the object at the permalink");
        ExtendableField<ThemeSnapshotSlugPageType>(
            "page",
            "A content page (Pages, ContentFile) as it was read; null for other object types and a page that was not read",
            resolve: context => context.Source.Page);
    }
}
