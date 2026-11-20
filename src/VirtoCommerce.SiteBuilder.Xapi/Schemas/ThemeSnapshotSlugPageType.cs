using VirtoCommerce.SiteBuilder.Xapi.Models;
using VirtoCommerce.Xapi.Core.Schemas;

namespace VirtoCommerce.SiteBuilder.Xapi.Schemas;

public class ThemeSnapshotSlugPageType : ExtendableGraphType<ThemeSnapshotSlugPage>
{
    public ThemeSnapshotSlugPageType()
    {
        Name = "ThemeSnapshotSlugPage";

        Field(x => x.Source, nullable: true).Description("A page from the database: its document's source. Null for a file page");
        Field(x => x.Permalink, nullable: true).Description("The page's permalink");
        Field(x => x.Settings, nullable: true).Description(
            "A JSON object of the page's settings that choose its template and layout: layout, template and the settings the page rules' setting conditions name. Null when the page's content is not a JSON object with a settings object");
    }
}
