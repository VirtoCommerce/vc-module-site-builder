using Newtonsoft.Json.Linq;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public class SharedComponentContentSaveOperationRequest : SharedComponentOperationRequest
{
    public JObject Content { get; set; }

    /// <summary>
    /// The version of the content the caller reviewed, as a dry run answers it in <see cref="SiteBuilderObjectChange.ETag"/>
    /// (<see cref="SiteBuilderContentVersion.CreateForFileContent"/> of the content as it is read): the save is refused once someone saved the
    /// content since. <c>null</c> writes over whatever it holds.
    /// </summary>
    public string ExpectedContentVersion { get; set; }
}
