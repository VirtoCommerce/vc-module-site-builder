using VirtoCommerce.SiteBuilder.Core.Theme;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>One custom section type as it is checked: what its parts share, and what the checks found.</summary>
public class CustomSectionCheck(string type, CustomSectionValidationResult result)
{
    public string Type { get; } = type;

    public string File { get; } = ThemeStorage.GetCustomSectionFile(type);

    public StorefrontKit Kit { get; set; }

    /// <summary>The settings a binding can name, by id; <c>null</c> when the schema cannot be read.</summary>
    public IDictionary<string, CustomSectionSetting> Settings { get; set; }

    public void AddError(string error) => result.Errors.Add(error);

    public void AddWarning(string warning) => result.Warnings.Add(warning);
}

/// <summary>A setting a binding can name; <see cref="Element"/> holds the fields of a list's rows or of an object.</summary>
/// <param name="Options">The values of a select's options.</param>
public sealed record CustomSectionSetting(string Id, string Type, bool IsLink, IReadOnlyDictionary<string, CustomSectionSetting> Element,
    IReadOnlyList<string> Options = null)
{
    /// <summary>The list of a data-each that names none: the items of its rows are not checked.</summary>
    public static CustomSectionSetting UnknownList { get; } = new(string.Empty, CustomSectionFields.ListType, IsLink: false, Element: null);
}
