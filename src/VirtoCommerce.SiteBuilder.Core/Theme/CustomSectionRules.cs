namespace VirtoCommerce.SiteBuilder.Core.Theme;

public class CustomSectionRules
{
    /// <summary>The regular expression a type's name matches.</summary>
    public string TypePattern { get; set; }

    public int MaxTypeLength { get; set; }

    /// <summary>The keys a section holds itself, which no setting takes as its id.</summary>
    public IList<string> ReservedSettingIds { get; set; } = [];

    /// <summary>Besides the kit tags the catalog names.</summary>
    public IList<string> Elements { get; set; } = [];

    /// <summary>Of any element, besides aria-*, the binding attributes and the catalog's behaviors.</summary>
    public IList<string> Attributes { get; set; } = [];

    /// <summary>By element: the attributes only it takes.</summary>
    public IDictionary<string, IList<string>> ElementAttributes { get; set; } = new Dictionary<string, IList<string>>();

    /// <summary>The storefront writes these data- attributes itself.</summary>
    public IList<string> ReservedDataPrefixes { get; set; } = [];

    public IList<string> BindingAttributes { get; set; } = [];

    /// <summary>Read by people, so a literal value is untranslated text.</summary>
    public IList<string> TextAttributes { get; set; } = [];

    /// <summary>Besides a path starting with a single '/' and an anchor on the page.</summary>
    public IList<string> LinkSchemes { get; set; } = [];

    public IList<string> LinkTargets { get; set; } = [];

    public IList<string> RefusedRels { get; set; } = [];

    /// <summary>What follows the type in a class of the type's own; the type itself is one too.</summary>
    public IList<string> ClassSeparators { get; set; } = [];

    public IList<string> SchemaKeys { get; set; } = [];

    public IList<string> FieldTypes { get; set; } = [];

    public IList<string> FieldKeys { get; set; } = [];

    public IList<string> AtRules { get; set; } = [];

    /// <summary>The at-rules a @keyframes may stand in, besides the top level.</summary>
    public IList<string> KeyframesHolders { get; set; } = [];

    /// <summary>What follows the type in a keyframes name, which is not scoped.</summary>
    public string KeyframesSeparator { get; set; }

    public IList<string> ScriptProperties { get; set; } = [];

    /// <summary>Functions refused anywhere: they run script or paint a live copy of an element of the page.</summary>
    public IList<string> RefusedFunctions { get; set; } = [];

    /// <summary>The only values of position.</summary>
    public IList<string> PositionValues { get; set; } = [];

    /// <summary>Their URL must be one of the asset library.</summary>
    public IList<string> ImageFunctions { get; set; } = [];

    /// <summary>Resolved only when rendered, as a custom --name() is: refused inside an image function and in position.</summary>
    public IList<string> LateValueFunctions { get; set; } = [];

    /// <summary>Physical to logical; a save warns of the physical one outside a one-direction rule such as :dir(rtl).</summary>
    public IDictionary<string, string> LogicalProperties { get; set; } = new Dictionary<string, string>();

    /// <summary>The types of a kit tag's attribute, of a behavior's option and of a behavior's own value.</summary>
    public IList<string> KitFieldTypes { get; set; } = [];

    /// <summary>The attributes no kit tag takes, whatever the catalog declares; a name ending with '*' is a start of names.</summary>
    public IList<string> KitDeniedAttributes { get; set; } = [];

    /// <summary>Checked as links, whatever type the catalog declares.</summary>
    public IList<string> KitLinkAttributes { get; set; } = [];

    /// <summary>Checked as images, whatever type the catalog declares.</summary>
    public IList<string> KitImageAttributes { get; set; } = [];

    /// <summary>The root counts as 1.</summary>
    public int MaxTemplateDepth { get; set; }

    /// <summary>Each '&lt;' followed by a letter in the template as written, in comments and attribute values too.</summary>
    public int MaxTemplateTags { get; set; }

    /// <summary>Of each tag, counted as written: a template with a tag of more is refused before it is parsed.</summary>
    public int MaxTemplateAttributes { get; set; }

    /// <summary>The caps of the catalog's kit, by what they count, as its upload holds it to them.</summary>
    public IDictionary<string, int> KitLimits { get; set; } = new Dictionary<string, int>();

    public int MaxCssDepth { get; set; }

    public int MaxAtRuleDepth { get; set; }

    /// <summary>UTF-8 bytes, for the template and the CSS each.</summary>
    public int MaxPartSize { get; set; }

    /// <summary>Bytes of JSON, for all the theme's types together.</summary>
    public int MaxTotalSize { get; set; }
}
