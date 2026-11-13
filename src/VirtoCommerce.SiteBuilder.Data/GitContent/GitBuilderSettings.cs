using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Data.GitContent;

public static class GitBuilderSettings
{
    /// <summary>
    /// The publish-related request descriptors for a store on the git flow. <c>unpublish</c> is one of
    /// them: with pages in git, taking a page down is deleting the file from the production branch, so
    /// it ships the same way a publish does — a commit on the work branch and a merge.
    /// <para>
    /// The same mechanism switches version history on: the panel exists only for a store whose pages
    /// are in git, and it learns where to ask from here rather than from the app's bundled config.
    /// </para>
    /// </summary>
    public static JObject GetDescriptors()
    {
        const string storeIdArg = "storeId={{location.params.storeId}}";

        // Every url carries the content type: pages and blogs are the same kind of file in two
        // folders, and the server cannot tell them apart from the path alone.
        const string pageArgs = "path={{path}}&type={{type}}";

        return new JObject
        {
            ["publish"] = new JObject
            {
                ["status"] = $"/api/site-builder/git/publish-status?{storeIdArg}&{pageArgs}",
                ["publish"] = new JObject
                {
                    ["url"] = $"/api/site-builder/git/publish?{storeIdArg}&{pageArgs}",
                    ["method"] = "POST",
                },
                ["unpublish"] = new JObject
                {
                    ["url"] = $"/api/site-builder/git/unpublish?{storeIdArg}&{pageArgs}",
                    ["method"] = "POST",
                },
                // Offered whether or not a release branch is configured; the status endpoint reports
                // production as null when there is none, and the client shows no production stage at
                // all. Gating the descriptor as well would mean two places to keep in agreement.
                ["promote"] = new JObject
                {
                    ["url"] = $"/api/site-builder/git/promote?{storeIdArg}&{pageArgs}",
                    ["method"] = "POST",
                },
            },
            ["externalPreview"] = new JObject
            {
                ["url"] = $"/api/site-builder/git/preview?{storeIdArg}&{pageArgs}",
            },
            ["history"] = new JObject
            {
                ["url"] = $"/api/site-builder/git/history?{storeIdArg}&{pageArgs}",
                // a version is addressed by its sha, which the panel substitutes per row
                ["preview"] = new JObject
                {
                    ["url"] = $"/api/site-builder/git/preview?{storeIdArg}&{pageArgs}&ref={{{{sha}}}}",
                },
                ["restore"] = new JObject
                {
                    ["url"] = $"/api/site-builder/git/restore-version?{storeIdArg}&{pageArgs}&sha={{{{sha}}}}",
                    ["method"] = "POST",
                },
            },
        };
    }
}
