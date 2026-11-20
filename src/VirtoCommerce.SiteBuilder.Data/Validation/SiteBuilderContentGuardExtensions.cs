using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VirtoCommerce.SiteBuilder.Core.Models;
using VirtoCommerce.SiteBuilder.Core.Services;
using VirtoCommerce.SiteBuilder.Data.Theme;

namespace VirtoCommerce.SiteBuilder.Data.Validation;

public static class SiteBuilderContentGuardExtensions
{
    /// <summary>The page content to store: as sent unless some of its HTML was sanitized. Adds what is refused to <paramref name="errors"/>.</summary>
    public static string GuardPageContent(this ISiteBuilderContentGuard guard, string content, string name, JObject catalog, ICollection<string> errors) =>
        Guard(content, token => guard.GuardPage(token, name, catalog), errors);

    /// <exception cref="InvalidDataException">Something in the content can run script.</exception>
    public static string GuardPageContent(this ISiteBuilderContentGuard guard, string content, string name, JObject catalog)
    {
        var errors = new List<string>();
        var result = guard.GuardPageContent(content, name, catalog, errors);
        ThrowIfAny(errors);

        return result;
    }

    /// <param name="file">The path in the theme folder.</param>
    public static string GuardThemeFileContent(this ISiteBuilderContentGuard guard, string file, string content, JObject catalog, ICollection<string> errors) =>
        Guard(content, token => guard.GuardThemeFile(file, token, catalog), errors);

    /// <exception cref="InvalidDataException">There are errors; every save path answers it with 400.</exception>
    public static void ThrowIfAny(ICollection<string> errors)
    {
        if (errors.Count > 0)
        {
            throw new InvalidDataException(GetMessage(errors));
        }
    }

    public static string GetMessage(IEnumerable<string> errors) => $"The content can run script in the storefront: {string.Join("; ", errors)}";

    /// <summary>Written again in the layout it came in: indented when it has more than one line.</summary>
    public static string Serialize(JToken token, string original) =>
        token.ToString(original?.Contains('\n') == true ? Formatting.Indented : Formatting.None);

    // content that is not JSON is left to the checks of its shape
    private static string Guard(string content, Func<JToken, SiteBuilderContentGuardResult> guard, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        JToken token;
        try
        {
            token = ThemeJson.Parse(content);
        }
        catch (JsonReaderException)
        {
            return content;
        }

        var result = guard(token);
        foreach (var error in result.Errors)
        {
            errors.Add(error);
        }

        return result.Changed ? Serialize(token, content) : content;
    }
}
