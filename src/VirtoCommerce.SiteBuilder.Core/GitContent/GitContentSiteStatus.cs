namespace VirtoCommerce.SiteBuilder.Core.GitContent;

public class GitContentSiteStatus
{
    /// <summary>GitHub is connected and usable.</summary>
    public bool Configured { get; set; }

    public string Organization { get; set; }

    /// <summary>
    /// Git content is on for the store. Without <see cref="Configured"/>, the store asks for it and its pages are
    /// refused until GitHub is connected or this is turned off.
    /// </summary>
    public bool Enabled { get; set; }

    public string Repository { get; set; }

    public string BaseBranch { get; set; }

    public string Url { get; set; }

    public string LastCommitSha { get; set; }

    public string Error { get; set; }

    public GitHubConnectionSource? Source { get; set; }

    public string InstallUrl { get; set; }
}
