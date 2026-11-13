namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>
/// A condition on the settings of a section: the value at <see cref="Path"/> equals <see cref="Value"/>, or contains it
/// ignoring case. A text per language (an object keyed by language codes) matches when the text of
/// <see cref="Language"/> matches, or, without a language, when the text of any language does.
/// </summary>
public class SiteBuilderSectionSettingCondition
{
    public const string EqualsOperator = "equals";
    public const string ContainsOperator = "contains";

    /// <summary>
    /// A setting of the section or a dotted path into nested values: "title", "button.url". In a list, a step is the id
    /// of an item, its 0-based index, or "*" for any item: "blocks.*.text".
    /// </summary>
    public string Path { get; set; }

    /// <summary><see cref="EqualsOperator"/> (exact) or <see cref="ContainsOperator"/> (ignoring case).</summary>
    public string Operator { get; set; } = EqualsOperator;

    /// <summary>A text; a number or a true/false value is compared as it is written in JSON.</summary>
    public string Value { get; set; }

    public string Language { get; set; }
}
