namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>The keys and the setting types of a custom section type's schema that its checks read.</summary>
public static class CustomSectionFields
{
    public const string SettingsKey = "settings";
    public const string FormatKey = "format";
    public const string UrlFormat = "url";

    public const string StringType = "string";
    public const string TextType = "text";
    public const string SelectType = "select";
    public const string ImagesType = "images";
    public const string ListType = "list";
    public const string ObjectType = "object";
    public const string NumberType = "number";
    public const string SliderType = "slider";
    public const string CheckboxType = "checkbox";
    public const string ColorTokenType = "color-token";

    /// <summary>What a color-token setting's token starts with; one without it is written with it.</summary>
    public const string ColorTokenPrefix = "color_";
}
