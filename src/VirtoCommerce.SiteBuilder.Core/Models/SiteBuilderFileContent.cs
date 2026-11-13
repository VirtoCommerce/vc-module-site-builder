namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A file as it is read: streamed as stored, or as text where it is made or read some other way.</summary>
public class SiteBuilderFileContent
{
    /// <summary>The file as stored, for the caller to dispose of; <c>null</c> when it is answered as <see cref="Text"/>.</summary>
    public Stream Stream { get; set; }

    /// <summary>The name of the stored file, which tells its media type.</summary>
    public string Name { get; set; }

    /// <summary>JSON.</summary>
    public string Text { get; set; }

    /// <summary>For a file that is not found: the blob storage folder it was looked for in.</summary>
    public string BasePath { get; set; }

    /// <summary>For a file that is not found: the version of the page kept in git it was looked for at.</summary>
    public string Ref { get; set; }
}
