namespace VirtoCommerce.SiteBuilder.Core.Theme;

/// <summary>The lists a custom section type is checked against, for an author to read before writing one.</summary>
public class CustomSectionRules
{
    /// <summary>The HTML elements a template may hold, besides the kit tags the storefront's catalog names.</summary>
    public IList<string> Elements { get; set; } = [];

    /// <summary>The attributes of an HTML element, besides aria-*, the binding attributes and the behaviors the storefront's catalog names.</summary>
    public IList<string> Attributes { get; set; } = [];

    /// <summary>The attributes that bind settings to elements: repeat, show and insert HTML.</summary>
    public IList<string> BindingAttributes { get; set; } = [];

    /// <summary>The attributes people read, as they read the template's text: their text is the same in every language unless it is bound.</summary>
    public IList<string> TextAttributes { get; set; } = [];

    /// <summary>The schemes a link written into the template may have, besides a path starting with '/'.</summary>
    public IList<string> LinkSchemes { get; set; } = [];

    public IList<string> SchemaKeys { get; set; } = [];

    public IList<string> FieldTypes { get; set; } = [];

    public IList<string> FieldKeys { get; set; } = [];

    public IList<string> AtRules { get; set; } = [];

    /// <summary>The bytes of the template, and of the CSS, at most.</summary>
    public int MaxPartSize { get; set; }

    /// <summary>The bytes of JSON of every type of a theme together at most.</summary>
    public int MaxTotalSize { get; set; }
}
