namespace VirtoCommerce.SiteBuilder.Core.Models;

/// <summary>A text per language matches on the text of <see cref="Language"/>, or, without one, on the text of any language.</summary>
public class SiteBuilderSectionSettingCondition
{
    public const string EqualsOperator = "equals";
    public const string ContainsOperator = "contains";

    /// <summary>A dotted path; in a list, a step is an item's id, its 0-based index, or "*" for any item.</summary>
    public string Path { get; set; }

    public string Operator { get; set; } = EqualsOperator;

    public string Value { get; set; }

    public string Language { get; set; }
}
