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

    /// <summary>The values of a link's target.</summary>
    public IList<string> LinkTargets { get; set; } = [];

    /// <summary>The values a link's rel does not hold.</summary>
    public IList<string> RefusedRels { get; set; } = [];

    /// <summary>What follows the type in a class of the type's own; the type itself is one too.</summary>
    public IList<string> ClassSeparators { get; set; } = [];

    public IList<string> SchemaKeys { get; set; } = [];

    public IList<string> FieldTypes { get; set; } = [];

    public IList<string> FieldKeys { get; set; } = [];

    public IList<string> AtRules { get; set; } = [];

    /// <summary>The CSS properties that run script.</summary>
    public IList<string> ScriptProperties { get; set; } = [];

    /// <summary>The CSS functions that load an image, whose URL is one of the asset library.</summary>
    public IList<string> ImageFunctions { get; set; } = [];

    /// <summary>The CSS functions whose value is known only when the page is shown: not inside an image function, not in position.</summary>
    public IList<string> LateValueFunctions { get; set; } = [];

    /// <summary>The logical property or value for each physical one, which a save warns of outside a rule for one direction such as :dir(rtl).</summary>
    public IDictionary<string, string> LogicalProperties { get; set; } = new Dictionary<string, string>();

    /// <summary>The types of a kit tag's attribute, of a behavior's option and of a behavior's own value.</summary>
    public IList<string> KitFieldTypes { get; set; } = [];

    /// <summary>The attributes no kit tag takes, whatever the catalog declares; a name ending with '*' is a start of names.</summary>
    public IList<string> KitDeniedAttributes { get; set; } = [];

    /// <summary>The attributes of a kit tag that follow the link rule, whatever type the catalog declares.</summary>
    public IList<string> KitLinkAttributes { get; set; } = [];

    /// <summary>The depth of the template's elements at most, the root counted.</summary>
    public int MaxTemplateDepth { get; set; }

    /// <summary>The depth of the CSS's blocks at most.</summary>
    public int MaxCssDepth { get; set; }

    /// <summary>The depth of the CSS's at-rules at most.</summary>
    public int MaxAtRuleDepth { get; set; }

    /// <summary>The bytes of the template, and of the CSS, at most.</summary>
    public int MaxPartSize { get; set; }

    /// <summary>The bytes of JSON of every type of a theme together at most.</summary>
    public int MaxTotalSize { get; set; }
}
