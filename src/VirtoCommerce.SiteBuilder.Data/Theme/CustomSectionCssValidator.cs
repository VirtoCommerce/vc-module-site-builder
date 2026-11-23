using System.Globalization;
using System.Text.RegularExpressions;
using static VirtoCommerce.SiteBuilder.Data.Theme.CustomSectionText;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>A custom section type's CSS, read from its tokens as a browser reads it: a comment in a string, an escape or a quote in a URL included.</summary>
public class CustomSectionCssValidator(CustomSectionUrlPolicy urlPolicy)
{
    private const int MaxSamples = 3;
    private const string KeyframesAtRule = "keyframes";
    private const string StyleRuleBlock = "";
    private const string KeyframeBlock = "keyframe";

    public const int MaxDepth = 32;
    public const int MaxAtRuleDepth = 4;

    /// <summary>What follows the type in a keyframes name, which is not scoped.</summary>
    public const string KeyframesSeparator = "__";

    public static IReadOnlyList<string> AtRules { get; } = ["media", "supports", "container", "starting-style", KeyframesAtRule];

    /// <summary>The at-rules a @keyframes may stand in, besides the top level; the storefront drops one anywhere else.</summary>
    public static IReadOnlyList<string> KeyframesHolders { get; } = ["media", "supports", "container"];

    public static IReadOnlySet<string> ScriptProperties { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "-moz-binding", "behavior" };

    /// <summary>By function: what it does, which the storefront drops it for.</summary>
    public static IReadOnlyDictionary<string, string> RefusedFunctions { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["expression"] = "runs script",
        ["element"] = "paints a live copy of an element of the page",
        ["-moz-element"] = "paints a live copy of an element of the page",
    };

    /// <summary>The only values of position the storefront keeps.</summary>
    public static IReadOnlyList<string> PositionValues { get; } = ["static", "relative", "absolute", "sticky", "inherit", "initial", "unset", "revert"];

    /// <summary>Their URL must be one of the asset library.</summary>
    public static IReadOnlySet<string> ImageFunctions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "url", "src", "image", "image-set", "-webkit-image-set", "cross-fade", "-webkit-cross-fade",
    };

    /// <summary>Resolved only when rendered, as a custom --name() is: refused inside an image function and in position.</summary>
    public static IReadOnlySet<string> LateValueFunctions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "var", "attr", "env", "if", "inherit" };

    /// <summary>Physical to logical; a save warns of the physical one outside a one-direction rule such as :dir(rtl).</summary>
    public static IReadOnlyDictionary<string, string> LogicalProperties { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["margin-left"] = "margin-inline-start",
        ["margin-right"] = "margin-inline-end",
        ["padding-left"] = "padding-inline-start",
        ["padding-right"] = "padding-inline-end",
        ["left"] = "inset-inline-start",
        ["right"] = "inset-inline-end",
        ["border-left"] = "border-inline-start",
        ["border-right"] = "border-inline-end",
        ["border-left-width"] = "border-inline-start-width",
        ["border-right-width"] = "border-inline-end-width",
        ["border-left-style"] = "border-inline-start-style",
        ["border-right-style"] = "border-inline-end-style",
        ["border-left-color"] = "border-inline-start-color",
        ["border-right-color"] = "border-inline-end-color",
        ["border-top-left-radius"] = "border-start-start-radius",
        ["border-top-right-radius"] = "border-start-end-radius",
        ["border-bottom-left-radius"] = "border-end-start-radius",
        ["border-bottom-right-radius"] = "border-end-end-radius",
        ["scroll-margin-left"] = "scroll-margin-inline-start",
        ["scroll-margin-right"] = "scroll-margin-inline-end",
        ["scroll-padding-left"] = "scroll-padding-inline-start",
        ["scroll-padding-right"] = "scroll-padding-inline-end",
    };

    /// <summary>By property: the logical values for left and right.</summary>
    public static IReadOnlyDictionary<string, (string Left, string Right)> LogicalValues { get; } = new Dictionary<string, (string Left, string Right)>(StringComparer.OrdinalIgnoreCase)
    {
        ["text-align"] = ("start", "end"),
        ["float"] = ("inline-start", "inline-end"),
        ["clear"] = ("inline-start", "inline-end"),
    };

    /// <summary>The parts each shorthand sets, as a browser lists them in a rule's declarations.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Shorthands { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["animation"] = "animation-duration animation-timing-function animation-delay animation-iteration-count animation-direction animation-fill-mode " +
            "animation-play-state animation-name animation-timeline animation-range-start animation-range-end",
        ["animation-range"] = "animation-range-start animation-range-end",
        ["background"] = "background-image background-position-x background-position-y background-size background-repeat background-attachment " +
            "background-origin background-clip background-color",
        ["background-position"] = "background-position-x background-position-y",
        ["border"] = "border-top-color border-top-style border-top-width border-right-color border-right-style border-right-width border-bottom-color " +
            "border-bottom-style border-bottom-width border-left-color border-left-style border-left-width border-image-source border-image-slice " +
            "border-image-width border-image-outset border-image-repeat",
        ["border-block"] = "border-block-start-color border-block-start-style border-block-start-width border-block-end-color border-block-end-style " +
            "border-block-end-width",
        ["border-block-color"] = "border-block-start-color border-block-end-color",
        ["border-block-end"] = "border-block-end-width border-block-end-style border-block-end-color",
        ["border-block-start"] = "border-block-start-width border-block-start-style border-block-start-color",
        ["border-block-style"] = "border-block-start-style border-block-end-style",
        ["border-block-width"] = "border-block-start-width border-block-end-width",
        ["border-bottom"] = "border-bottom-width border-bottom-style border-bottom-color",
        ["border-color"] = "border-top-color border-right-color border-bottom-color border-left-color",
        ["border-image"] = "border-image-source border-image-slice border-image-width border-image-outset border-image-repeat",
        ["border-inline"] = "border-inline-start-color border-inline-start-style border-inline-start-width border-inline-end-color border-inline-end-style " +
            "border-inline-end-width",
        ["border-inline-color"] = "border-inline-start-color border-inline-end-color",
        ["border-inline-end"] = "border-inline-end-width border-inline-end-style border-inline-end-color",
        ["border-inline-start"] = "border-inline-start-width border-inline-start-style border-inline-start-color",
        ["border-inline-style"] = "border-inline-start-style border-inline-end-style",
        ["border-inline-width"] = "border-inline-start-width border-inline-end-width",
        ["border-left"] = "border-left-width border-left-style border-left-color",
        ["border-radius"] = "border-top-left-radius border-top-right-radius border-bottom-right-radius border-bottom-left-radius",
        ["border-right"] = "border-right-width border-right-style border-right-color",
        ["border-style"] = "border-top-style border-right-style border-bottom-style border-left-style",
        ["border-top"] = "border-top-width border-top-style border-top-color",
        ["border-width"] = "border-top-width border-right-width border-bottom-width border-left-width",
        ["column-rule"] = "column-rule-width column-rule-style column-rule-color",
        ["columns"] = "column-width column-count column-height column-wrap",
        ["contain-intrinsic-size"] = "contain-intrinsic-width contain-intrinsic-height",
        ["container"] = "container-name container-type",
        ["flex"] = "flex-grow flex-shrink flex-basis",
        ["flex-flow"] = "flex-direction flex-wrap",
        ["font"] = "font-style font-variant-ligatures font-variant-caps font-variant-numeric font-variant-east-asian font-variant-alternates " +
            "font-variant-position font-variant-emoji font-weight font-stretch font-size line-height font-family font-optical-sizing font-size-adjust " +
            "font-kerning font-feature-settings font-variation-settings font-language-override",
        ["font-synthesis"] = "font-synthesis-weight font-synthesis-style font-synthesis-small-caps",
        ["font-variant"] = "font-variant-ligatures font-variant-caps font-variant-alternates font-variant-numeric font-variant-east-asian " +
            "font-variant-position font-variant-emoji",
        ["gap"] = "row-gap column-gap",
        ["grid"] = "grid-template-rows grid-template-columns grid-template-areas grid-auto-flow grid-auto-rows grid-auto-columns",
        ["grid-area"] = "grid-row-start grid-column-start grid-row-end grid-column-end",
        ["grid-column"] = "grid-column-start grid-column-end",
        ["grid-row"] = "grid-row-start grid-row-end",
        ["grid-template"] = "grid-template-rows grid-template-columns grid-template-areas",
        ["inset"] = "top right bottom left",
        ["inset-block"] = "inset-block-start inset-block-end",
        ["inset-inline"] = "inset-inline-start inset-inline-end",
        ["list-style"] = "list-style-position list-style-image list-style-type",
        ["margin"] = "margin-top margin-right margin-bottom margin-left",
        ["margin-block"] = "margin-block-start margin-block-end",
        ["margin-inline"] = "margin-inline-start margin-inline-end",
        ["mask"] = "mask-image -webkit-mask-position-x -webkit-mask-position-y mask-size mask-repeat mask-origin mask-clip mask-composite mask-mode",
        ["offset"] = "offset-position offset-path offset-distance offset-rotate offset-anchor",
        ["outline"] = "outline-color outline-style outline-width",
        ["overflow"] = "overflow-x overflow-y",
        ["overscroll-behavior"] = "overscroll-behavior-x overscroll-behavior-y",
        ["padding"] = "padding-top padding-right padding-bottom padding-left",
        ["padding-block"] = "padding-block-start padding-block-end",
        ["padding-inline"] = "padding-inline-start padding-inline-end",
        ["place-content"] = "align-content justify-content",
        ["place-items"] = "align-items justify-items",
        ["place-self"] = "align-self justify-self",
        ["position-try"] = "position-try-order position-try-fallbacks",
        ["scroll-margin"] = "scroll-margin-top scroll-margin-right scroll-margin-bottom scroll-margin-left",
        ["scroll-margin-block"] = "scroll-margin-block-start scroll-margin-block-end",
        ["scroll-margin-inline"] = "scroll-margin-inline-start scroll-margin-inline-end",
        ["scroll-padding"] = "scroll-padding-top scroll-padding-right scroll-padding-bottom scroll-padding-left",
        ["scroll-padding-block"] = "scroll-padding-block-start scroll-padding-block-end",
        ["scroll-padding-inline"] = "scroll-padding-inline-start scroll-padding-inline-end",
        ["scroll-timeline"] = "scroll-timeline-name scroll-timeline-axis",
        ["text-box"] = "text-box-trim text-box-edge",
        ["text-decoration"] = "text-decoration-line text-decoration-thickness text-decoration-style text-decoration-color",
        ["text-emphasis"] = "text-emphasis-style text-emphasis-color",
        ["text-wrap"] = "text-wrap-mode text-wrap-style",
        ["transition"] = "transition-property transition-duration transition-timing-function transition-delay transition-behavior",
        ["view-timeline"] = "view-timeline-name view-timeline-axis view-timeline-inset",
        ["white-space"] = "white-space-collapse text-wrap-mode",
        ["-webkit-mask"] = "mask-image -webkit-mask-position-x -webkit-mask-position-y mask-size mask-repeat mask-origin mask-clip mask-composite mask-mode",
        ["-webkit-text-stroke"] = "-webkit-text-stroke-width -webkit-text-stroke-color",
    }.ToDictionary(x => x.Key, IReadOnlySet<string> (x) => x.Value.Split(' ').ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    // legacy names a browser parses as the property they alias, so the rule is written back as that property's
    private static readonly Dictionary<string, string> PropertyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["grid-gap"] = "gap",
        ["grid-row-gap"] = "row-gap",
        ["grid-column-gap"] = "column-gap",
        ["-webkit-animation"] = "animation",
        ["-webkit-border-radius"] = "border-radius",
        ["-webkit-column-rule"] = "column-rule",
        ["-webkit-columns"] = "columns",
        ["-webkit-flex"] = "flex",
        ["-webkit-flex-flow"] = "flex-flow",
        ["-webkit-text-emphasis"] = "text-emphasis",
        ["-webkit-transition"] = "transition",
    };

    private static readonly HashSet<string> ColorFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "rgb", "rgba", "hsl", "hsla", "hwb", "lab", "lch", "oklab", "oklch", "color",
    };

    // CSS Color 4's named colors, without transparent and currentcolor, which are not literal colors of a palette
    private static readonly HashSet<string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "aliceblue", "antiquewhite", "aqua", "aquamarine", "azure", "beige", "bisque", "black", "blanchedalmond", "blue", "blueviolet", "brown",
        "burlywood", "cadetblue", "chartreuse", "chocolate", "coral", "cornflowerblue", "cornsilk", "crimson", "cyan", "darkblue", "darkcyan",
        "darkgoldenrod", "darkgray", "darkgreen", "darkgrey", "darkkhaki", "darkmagenta", "darkolivegreen", "darkorange", "darkorchid", "darkred",
        "darksalmon", "darkseagreen", "darkslateblue", "darkslategray", "darkslategrey", "darkturquoise", "darkviolet", "deeppink", "deepskyblue",
        "dimgray", "dimgrey", "dodgerblue", "firebrick", "floralwhite", "forestgreen", "fuchsia", "gainsboro", "ghostwhite", "gold", "goldenrod",
        "gray", "green", "greenyellow", "grey", "honeydew", "hotpink", "indianred", "indigo", "ivory", "khaki", "lavender", "lavenderblush",
        "lawngreen", "lemonchiffon", "lightblue", "lightcoral", "lightcyan", "lightgoldenrodyellow", "lightgray", "lightgreen", "lightgrey",
        "lightpink", "lightsalmon", "lightseagreen", "lightskyblue", "lightslategray", "lightslategrey", "lightsteelblue", "lightyellow", "lime",
        "limegreen", "linen", "magenta", "maroon", "mediumaquamarine", "mediumblue", "mediumorchid", "mediumpurple", "mediumseagreen",
        "mediumslateblue", "mediumspringgreen", "mediumturquoise", "mediumvioletred", "midnightblue", "mintcream", "mistyrose", "moccasin",
        "navajowhite", "navy", "oldlace", "olive", "olivedrab", "orange", "orangered", "orchid", "palegoldenrod", "palegreen", "paleturquoise",
        "palevioletred", "papayawhip", "peachpuff", "peru", "pink", "plum", "powderblue", "purple", "rebeccapurple", "red", "rosybrown",
        "royalblue", "saddlebrown", "salmon", "sandybrown", "seagreen", "seashell", "sienna", "silver", "skyblue", "slateblue", "slategray",
        "slategrey", "snow", "springgreen", "steelblue", "tan", "teal", "thistle", "tomato", "turquoise", "violet", "wheat", "white", "whitesmoke",
        "yellow", "yellowgreen",
    };

    // the styles that draw an outline: the shorthand sets one it does not name to none
    private static readonly HashSet<string> OutlineStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        "auto", "dotted", "dashed", "solid", "double", "groove", "ridge", "inset", "outset",
    };

    private static readonly HashSet<string> MotionProperties = new(StringComparer.OrdinalIgnoreCase) { "animation", "animation-name", "transition" };

    private static readonly HashSet<string> ReducedMotionValues = new(StringComparer.OrdinalIgnoreCase) { "reduce", "no-preference" };

    // the properties whose value may hold a color by its name; anywhere else a name such as tan may be anything
    private static readonly Regex ColorPropertyPattern = new("color|^background|^border|^outline|^column-rule|^text-decoration|shadow$|^fill$|^stroke$|^filter$",
        RegexOptions.None, TimeSpan.FromSeconds(1));

    // the number of a number or a dimension as CSS reads it, its exponent included
    private static readonly Regex NumberPattern = new(@"^[+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?", RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>The storefront wraps the CSS in blocks of its own, so the block structure is checked before anything else.</summary>
    public virtual void Validate(CustomSectionCheck check, string css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return;
        }

        var tokens = CssTokenizer.Tokenize(css);
        if (GetCssStructureError(tokens) is { } error)
        {
            check.AddError(error);
            return;
        }

        ValidateCssTokens(check, tokens);
    }

    /// <returns>The error of a token no browser reads as written, of a block without its pair or nested too deep; <c>null</c> for none.</returns>
    protected virtual string GetCssStructureError(IList<CssTokenizer.Token> tokens)
    {
        var blocks = new Stack<(CssTokenizer.TokenKind Closer, bool AtRule)>();
        var atRules = 0;
        var rules = 0;
        var inAtRulePrelude = false;
        foreach (var token in tokens)
        {
            // inside a function, a '(' or a '[', a ';' or '{' is part of it: it ends no at-rule's prelude and opens no rule
            var ruleLevel = blocks.Count == rules;
            switch (token.Kind)
            {
                case CssTokenizer.TokenKind.BadString:
                    return "the CSS has a string that a line break cuts off";
                case CssTokenizer.TokenKind.BadUrl:
                    return "the CSS has a url( whose URL a quote, a parenthesis or a space breaks: quote the URL";
                case CssTokenizer.TokenKind.AtKeyword:
                    inAtRulePrelude = true;
                    break;
                case CssTokenizer.TokenKind.Semicolon when ruleLevel:
                    inAtRulePrelude = false;
                    break;
                case CssTokenizer.TokenKind.OpenCurly when ruleLevel:
                    blocks.Push((CssTokenizer.TokenKind.CloseCurly, inAtRulePrelude));
                    rules++;
                    atRules += inAtRulePrelude ? 1 : 0;
                    inAtRulePrelude = false;
                    break;
                case CssTokenizer.TokenKind.OpenCurly:
                    blocks.Push((CssTokenizer.TokenKind.CloseCurly, false));
                    break;
                case CssTokenizer.TokenKind.OpenParen or CssTokenizer.TokenKind.Function:
                    blocks.Push((CssTokenizer.TokenKind.CloseParen, false));
                    break;
                case CssTokenizer.TokenKind.OpenSquare:
                    blocks.Push((CssTokenizer.TokenKind.CloseSquare, false));
                    break;
                case CssTokenizer.TokenKind.CloseCurly or CssTokenizer.TokenKind.CloseParen or CssTokenizer.TokenKind.CloseSquare:
                    if (blocks.Count == 0 || blocks.Peek().Closer != token.Kind)
                    {
                        return $"the CSS has a '{token.Value}' without its pair";
                    }

                    atRules -= blocks.Pop().AtRule ? 1 : 0;
                    if (ruleLevel)
                    {
                        rules--;
                        inAtRulePrelude = false;
                    }

                    break;
            }

            if (blocks.Count > MaxDepth)
            {
                return string.Create(CultureInfo.InvariantCulture, $"the CSS nests blocks deeper than {MaxDepth}");
            }

            if (atRules > MaxAtRuleDepth)
            {
                return string.Create(CultureInfo.InvariantCulture, $"the CSS nests at-rules deeper than {MaxAtRuleDepth}");
            }
        }

        return blocks.Count == 0 ? null : $"the CSS has a '{GetOpener(blocks.Peek().Closer)}' without its pair";
    }

    /// <summary>The at-rules, the URLs, and the declarations that run script or take the section out of the page, custom properties included.</summary>
    protected virtual void ValidateCssTokens(CustomSectionCheck check, IList<CssTokenizer.Token> tokens)
    {
        var findings = new CssFindings();
        var blocks = new Stack<CssTokenizer.Token>();
        var imageFunctions = new Stack<string>();
        // whether each open rule block is for one direction, as :dir(rtl) { } is
        var directionRules = new Stack<bool>();
        var preludeNamesDirection = false;
        // a declaration starts the CSS or a block, or follows a ';' or a nested rule; an ident anywhere else is part of a value or a selector
        var atDeclarationStart = true;
        var prelude = new List<CssTokenizer.Token>();
        // what opened each block: an at-rule's name, a keyframe, or a style rule
        var openers = new Stack<string>();
        // the shorthands with a late value of each open block's declarations that no later one has set a part of
        var lateShorthands = new Stack<List<LateShorthand>>();
        var colorSettings = (check.Settings?.Values ?? []).Where(x => x.Type == CustomSectionFields.ColorTokenType)
            .Select(x => "--setting-" + x.Id.Replace('_', '-'))
            .ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var block = blocks.Count > 0 ? blocks.Peek() : default;
            // only rule blocks are open: inside a function, a '(' or a '[', a ';', '{' or '}' is part of a prelude or a value, as a browser reads it
            var ruleLevel = blocks.Count == openers.Count;
            switch (token.Kind)
            {
                case CssTokenizer.TokenKind.Function when string.Equals(token.Value, "dir", StringComparison.OrdinalIgnoreCase):
                    preludeNamesDirection = true;
                    break;
                case CssTokenizer.TokenKind.Semicolon or CssTokenizer.TokenKind.CloseCurly when ruleLevel:
                    preludeNamesDirection = false;
                    break;
                case CssTokenizer.TokenKind.AtKeyword:
                    ValidateCssAtRule(check, tokens, i, findings);
                    break;
                case CssTokenizer.TokenKind.Url:
                    AddCssUrlError(token.Value, findings);
                    break;
                case CssTokenizer.TokenKind.String when IsImageArgument(blocks):
                    AddCssUrlError(token.Value, findings);
                    break;
                case CssTokenizer.TokenKind.Function when RefusedFunctions.TryGetValue(token.Value, out var refused):
                    findings.Errors.Add($"the CSS has {token.Value.ToLowerInvariant()}(), which {refused}");
                    break;
                case CssTokenizer.TokenKind.Function when IsLateValueFunction(token.Value) && imageFunctions.Count > 0:
                    findings.Errors.Add($"the CSS has {token.Value.ToLowerInvariant()}() inside {imageFunctions.Peek()}(), whose URL is then not known on save");
                    break;
                case CssTokenizer.TokenKind.Ident when blocks.Count == 0 && atDeclarationStart && ReadDeclarationValue(tokens, i) != null:
                    // a browser that reads no declaration directly in @scope drops it
                    findings.Errors.Add($"the CSS has the declaration '{token.Value}' outside any rule: write it in :scope {{ }}");
                    break;
                case CssTokenizer.TokenKind.Ident when block.Kind == CssTokenizer.TokenKind.OpenCurly && atDeclarationStart:
                    ValidateCssDeclaration(tokens, i, findings, inDirectionRule: directionRules.Count > 0 && directionRules.Peek());
                    ValidateCssShorthandParts(tokens, i, lateShorthands.Peek(), findings);
                    ValidateCssFocusRing(tokens, i, colorSettings, findings);
                    break;
            }

            if (token.Kind != CssTokenizer.TokenKind.Whitespace)
            {
                atDeclarationStart = ruleLevel && token.Kind is CssTokenizer.TokenKind.OpenCurly or CssTokenizer.TokenKind.Semicolon or CssTokenizer.TokenKind.CloseCurly;
            }

            if (ruleLevel && token.Kind == CssTokenizer.TokenKind.OpenCurly)
            {
                directionRules.Push((directionRules.Count > 0 && directionRules.Peek()) || preludeNamesDirection);
                preludeNamesDirection = false;
                ValidateCssSelector(prelude, findings);
                var opener = GetBlockOpener(prelude, openers);
                if (GetCssBlockError(opener, openers) is { } blockError)
                {
                    findings.Errors.Add(blockError);
                }

                openers.Push(opener);
                lateShorthands.Push([]);
            }

            if (ruleLevel && token.Kind is CssTokenizer.TokenKind.OpenCurly or CssTokenizer.TokenKind.CloseCurly or CssTokenizer.TokenKind.Semicolon)
            {
                prelude.Clear();
            }
            else
            {
                prelude.Add(token);
            }

            if (token.Kind is CssTokenizer.TokenKind.OpenCurly or CssTokenizer.TokenKind.OpenParen or CssTokenizer.TokenKind.OpenSquare or
                CssTokenizer.TokenKind.Function)
            {
                blocks.Push(token);
                if (token.Kind == CssTokenizer.TokenKind.Function && ImageFunctions.Contains(token.Value))
                {
                    imageFunctions.Push(token.Value.ToLowerInvariant());
                }
            }
            else if (token.Kind is CssTokenizer.TokenKind.CloseCurly or CssTokenizer.TokenKind.CloseParen or CssTokenizer.TokenKind.CloseSquare)
            {
                var closed = blocks.Pop();
                if (closed.Kind == CssTokenizer.TokenKind.Function && ImageFunctions.Contains(closed.Value))
                {
                    imageFunctions.Pop();
                }

                if (ruleLevel && token.Kind == CssTokenizer.TokenKind.CloseCurly)
                {
                    directionRules.Pop();
                    openers.Pop();
                    lateShorthands.Pop();
                    // the declarations after a nested rule are a block of their own
                    if (lateShorthands.Count > 0)
                    {
                        lateShorthands.Peek().Clear();
                    }
                }
            }
        }

        foreach (var error in findings.Errors.Distinct(StringComparer.Ordinal))
        {
            check.AddError(error);
        }

        foreach (var direction in findings.Directions.Distinct(StringComparer.Ordinal))
        {
            check.AddWarning(direction);
        }

        if (findings.Animates && !HasReducedMotionQuery(tokens))
        {
            check.AddWarning("the CSS animates with no @media (prefers-reduced-motion: reduce) or (prefers-reduced-motion: no-preference) block, so it moves " +
                "for people who ask for less motion");
        }

        AddColorWarning(check, findings);
    }

    /// <summary>
    /// A rule's selector may not name :root, html or body, nor may any other part of it match outside the section
    /// (<see cref="CustomSectionCssSelector"/>): the storefront drops such a rule.
    /// </summary>
    protected virtual void ValidateCssSelector(IList<CssTokenizer.Token> prelude, CssFindings findings)
    {
        var first = prelude.FirstOrDefault(x => x.Kind != CssTokenizer.TokenKind.Whitespace);
        if (first.Kind is CssTokenizer.TokenKind.None or CssTokenizer.TokenKind.AtKeyword)
        {
            return;
        }

        if (CustomSectionCssSelector.GetOutsideMatch(prelude) is { } outside)
        {
            findings.Errors.Add($"the CSS selector '{Quote(WriteCss(prelude))}' {outside}");
        }
    }

    /// <param name="opener">What opens the block: an at-rule's name, a keyframe, or a style rule.</param>
    /// <param name="enclosing">What opened each enclosing block, the innermost first.</param>
    /// <returns>Why the block may not stand there; <c>null</c> when it may.</returns>
    protected virtual string GetCssBlockError(string opener, IEnumerable<string> enclosing)
    {
        var parent = enclosing.FirstOrDefault();
        if (parent == KeyframesAtRule)
        {
            return opener == KeyframeBlock ? null : $"the CSS has {DescribeBlock(opener)} inside @keyframes, which holds only its keyframes, such as from {{ }} or 50% {{ }}";
        }

        if (parent == KeyframeBlock)
        {
            return "the CSS has a block inside a keyframe of @keyframes, which holds only declarations";
        }

        if (opener == KeyframesAtRule && enclosing.FirstOrDefault(x => !KeyframesHolders.Contains(x)) is { } holder)
        {
            return $"the CSS has @keyframes inside {DescribeBlock(holder)}, where the storefront drops it: a @keyframes stands at the top level or inside " +
                JoinOr(KeyframesHolders.Select(x => "@" + x));
        }

        return null;
    }

    protected virtual void ValidateCssAtRule(CustomSectionCheck check, IList<CssTokenizer.Token> tokens, int index, CssFindings findings)
    {
        var name = tokens[index].Value;
        if (!AtRules.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            findings.Errors.Add($"the CSS has @{name}, and a custom section's CSS has only {JoinAnd(AtRules.Select(x => "@" + x))}");
            return;
        }

        if (!string.Equals(name, KeyframesAtRule, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // a type's name may start another's, hence the separator
        var prefix = check.Type + KeyframesSeparator;
        var keyframes = tokens.Skip(index + 1).FirstOrDefault(x => x.Kind != CssTokenizer.TokenKind.Whitespace);
        if (keyframes.Kind is not (CssTokenizer.TokenKind.Ident or CssTokenizer.TokenKind.String))
        {
            findings.Errors.Add("the CSS has @keyframes without a name");
        }
        else if (keyframes.Value == prefix)
        {
            findings.Errors.Add($"the CSS names @keyframes '{Quote(keyframes.Value)}', the type and {KeyframesSeparator} with no name after them");
        }
        else if (!keyframes.Value.StartsWith(prefix, StringComparison.Ordinal))
        {
            findings.Errors.Add($"the CSS names @keyframes '{Quote(keyframes.Value)}', which does not start with '{prefix}': keyframes names are not scoped");
        }
    }

    /// <param name="inDirectionRule">In a rule for one direction, where a side of the page is meant.</param>
    protected virtual void ValidateCssDeclaration(IList<CssTokenizer.Token> tokens, int index, CssFindings findings, bool inDirectionRule)
    {
        if (ReadDeclarationValue(tokens, index) is not { } value)
        {
            return;
        }

        var name = tokens[index].Value;
        findings.Animates |= MotionProperties.Contains(name);
        if (HasLiteralColor(name, value))
        {
            findings.ColorProperties.Add(name.ToLowerInvariant());
        }

        if (!inDirectionRule && GetLogicalDirection(name, value) is { } logical)
        {
            findings.Directions.Add($"the CSS sets {logical.Physical}, which stays on that side when the page reads right to left: use {logical.Logical}");
        }

        if (ScriptProperties.Contains(name))
        {
            findings.Errors.Add($"the CSS sets {name.ToLowerInvariant()}, which runs script");
        }
        else if (string.Equals(name, "position", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Exists(x => x.Kind == CssTokenizer.TokenKind.Ident && string.Equals(x.Value, "fixed", StringComparison.OrdinalIgnoreCase)))
            {
                findings.Errors.Add("the CSS has position: fixed, which puts the section over the page");
            }
            else if (value.Find(x => x.Kind == CssTokenizer.TokenKind.Function && IsLateValueFunction(x.Value)) is { Value: { } function })
            {
                findings.Errors.Add($"the CSS sets position with {function.ToLowerInvariant()}(), which could make it fixed");
            }
            else if (!PositionValues.Contains(GetKeyword(value), StringComparer.OrdinalIgnoreCase))
            {
                findings.Errors.Add($"the CSS sets position to '{Quote(WriteCss(value))}', and a custom section's position is {JoinOr(PositionValues)}");
            }
        }
    }

    /// <summary>
    /// A value a save cannot read may make the ring none or 0, or the declaration invalid, which leaves the outline its initial style, none. So
    /// the width and the style take no function, and the outline's color comes from the palette, as the type's other colors do.
    /// </summary>
    /// <param name="colorSettings">The variables of the type's color-token settings, such as --setting-tone.</param>
    protected virtual void ValidateCssFocusRing(IList<CssTokenizer.Token> tokens, int index, IReadOnlySet<string> colorSettings, CssFindings findings)
    {
        if (ReadDeclarationValue(tokens, index) is not { } value)
        {
            return;
        }

        var name = tokens[index].Value.ToLowerInvariant();
        // a function's arguments, such as the 0 of hsl(0 100% 50%), set no part of the ring: GetFocusRingFunction judges the function
        var own = GetOwnTokens(value);
        var keyword = GetKeyword(value);
        if (IsFocusRingVariable(tokens[index].Value, colorSettings))
        {
            findings.Errors.Add($"the CSS sets {tokens[index].Value}, a variable the focus ring may read, which could hide where the keyboard is on the page");
        }
        else if (RemovesFocusRing(name, own))
        {
            findings.Errors.Add($"the CSS sets {name} to none or 0, which hides where the keyboard is on the page");
        }
        else if (name is "outline" or "outline-color" && own.Exists(IsTransparent))
        {
            findings.Errors.Add($"the CSS sets {name} to transparent, which hides where the keyboard is on the page");
        }
        else if (keyword != null && IsHidingKeyword(name, keyword))
        {
            findings.Errors.Add($"the CSS sets {name} to {keyword.ToLowerInvariant()}, which could hide where the keyboard is on the page");
        }
        else if (GetFocusRingFunction(name, value, colorSettings) is { } function)
        {
            findings.Errors.Add($"the CSS sets {name} with {function.ToLowerInvariant()}(), which could hide where the keyboard is on the page");
        }
        else if (name == "outline" && keyword?.ToLowerInvariant() is not ("revert" or "revert-layer") &&
            !own.Exists(x => x.Kind == CssTokenizer.TokenKind.Ident && OutlineStyles.Contains(x.Value)))
        {
            findings.Errors.Add("the CSS sets outline without a style, which the shorthand then sets to none, hiding where the keyboard is on the page: " +
                "write one, such as solid");
        }
    }

    /// <summary>
    /// A browser keeps a shorthand whose value holds var() as written, and its parts read empty until the page applies it. The storefront
    /// writes each rule back from what the browser parsed, so a later declaration of the same block that sets only some of those parts leaves
    /// the rule with those parts alone, and the shorthand is lost.
    /// </summary>
    /// <param name="earlier">The block's shorthands with a late value that no later declaration has set a part of; a shorthand with one is added.</param>
    protected virtual void ValidateCssShorthandParts(IList<CssTokenizer.Token> tokens, int index, List<LateShorthand> earlier, CssFindings findings)
    {
        if (ReadDeclarationValue(tokens, index) is not { } value)
        {
            return;
        }

        var name = tokens[index].Value.ToLowerInvariant();
        var property = PropertyAliases.GetValueOrDefault(name, name);
        var parts = GetShorthandParts(property);
        var important = IsImportant(value);
        var reached = earlier.FindAll(x => (important || !x.Important) && x.Parts.Overlaps(parts));
        foreach (var shorthand in reached.Where(x => !parts.IsSupersetOf(x.Parts)))
        {
            findings.Errors.Add($"the CSS sets {name} after {shorthand.Name} with {shorthand.Function}() in the same rule, and the storefront then " +
                $"loses {shorthand.Name}: set each of its parts on its own, such as {shorthand.Parts.First(x => !parts.Contains(x))}");
        }

        earlier.RemoveAll(reached.Contains);

        if (Shorthands.ContainsKey(property) && value.Find(x => x.Kind == CssTokenizer.TokenKind.Function && IsLateValueFunction(x.Value)) is { Value: { } function })
        {
            earlier.Add(new LateShorthand(name, function.ToLowerInvariant(), parts, important));
        }
    }

    /// <summary>A vendor-prefixed property, such as -webkit-background-clip, also sets the property it stands for.</summary>
    private static IReadOnlySet<string> GetShorthandParts(string name)
    {
        if (Shorthands.TryGetValue(name, out var parts))
        {
            return parts;
        }

        var unprefixed = name.StartsWith('-') && !name.StartsWith("--", StringComparison.Ordinal) && name.IndexOf('-', 1) is > 0 and var end
            ? name[(end + 1)..]
            : name;
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name, unprefixed };
    }

    private static bool IsImportant(List<CssTokenizer.Token> value)
    {
        var tokens = value.Where(x => x.Kind != CssTokenizer.TokenKind.Whitespace).ToList();
        return tokens.Count >= 2 && tokens[^2] is { Kind: CssTokenizer.TokenKind.Delim, Value: "!" } &&
            tokens[^1] is { Kind: CssTokenizer.TokenKind.Ident } important && string.Equals(important.Value, "important", StringComparison.OrdinalIgnoreCase);
    }

    /// <returns>The one keyword a value is, !important aside; <c>null</c> for any other value.</returns>
    private static string GetKeyword(List<CssTokenizer.Token> value)
    {
        var tokens = value.Where(x => x.Kind != CssTokenizer.TokenKind.Whitespace).ToList();
        if (tokens.Count == 3 && tokens[1] is { Kind: CssTokenizer.TokenKind.Delim, Value: "!" } &&
            tokens[2] is { Kind: CssTokenizer.TokenKind.Ident } important && string.Equals(important.Value, "important", StringComparison.OrdinalIgnoreCase))
        {
            tokens.RemoveRange(1, 2);
        }

        return tokens is [{ Kind: CssTokenizer.TokenKind.Ident } keyword] ? keyword.Value : null;
    }

    protected virtual void AddColorWarning(CustomSectionCheck check, CssFindings findings)
    {
        if (findings.ColorProperties.Count > 0)
        {
            var properties = string.Join(", ", findings.ColorProperties.Distinct(StringComparer.Ordinal).Take(MaxSamples));
            check.AddWarning($"the CSS sets literal colors ({properties}), which the palette does not change: use var(--color-…) or a color-token setting's variable");
        }
    }

    protected virtual void AddCssUrlError(string url, CssFindings findings)
    {
        if (!urlPolicy.IsAssetUrl(url) && !urlPolicy.IsImageDataUrl(url))
        {
            findings.Errors.Add($"the CSS loads '{Quote(url)}', which is not a URL of the store's asset library or a data:image/ URL without a raw #");
        }
    }

    private static (string Physical, string Logical)? GetLogicalDirection(string name, List<CssTokenizer.Token> value)
    {
        var property = name.ToLowerInvariant();
        if (LogicalProperties.TryGetValue(property, out var logicalProperty))
        {
            return (property, logicalProperty);
        }

        if (!LogicalValues.TryGetValue(property, out var logicalValues))
        {
            return null;
        }

        var side = value.Find(x => x.Kind == CssTokenizer.TokenKind.Ident && x.Value.ToLowerInvariant() is "left" or "right").Value?.ToLowerInvariant();

        return side == null ? null : ($"{property}: {side}", $"{property}: {(side == "left" ? logicalValues.Left : logicalValues.Right)}");
    }

    // a mask takes only the alpha of its colors, so black and transparent there are no colors of the design
    private static bool HasLiteralColor(string name, List<CssTokenizer.Token> value)
    {
        var property = name.ToLowerInvariant();
        if ((property.StartsWith("-webkit-", StringComparison.Ordinal) ? property[8..] : property).StartsWith("mask", StringComparison.Ordinal))
        {
            return false;
        }

        var takesNamedColors = !property.StartsWith("--", StringComparison.Ordinal) && ColorPropertyPattern.IsMatch(property);
        for (var i = 0; i < value.Count; i++)
        {
            var token = value[i];
            var isLiteral = token.Kind switch
            {
                CssTokenizer.TokenKind.Hash => true,
                CssTokenizer.TokenKind.Function => ColorFunctions.Contains(token.Value) && !IsPaletteRelativeColor(value, i),
                CssTokenizer.TokenKind.Ident => takesNamedColors && NamedColors.Contains(token.Value),
                _ => false,
            };
            if (isLiteral)
            {
                return true;
            }
        }

        return false;
    }

    // a relative color from the palette, rgb(from var(--color-primary-500) r g b / 0.25), changes with it
    private static bool IsPaletteRelativeColor(List<CssTokenizer.Token> value, int function)
    {
        var arguments = value.Skip(function + 1).Where(x => x.Kind != CssTokenizer.TokenKind.Whitespace).Take(3).ToList();
        if (arguments.Count < 2 || arguments[0].Kind != CssTokenizer.TokenKind.Ident || !string.Equals(arguments[0].Value, "from", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return arguments[1] switch
        {
            { Kind: CssTokenizer.TokenKind.Ident } origin => origin.Value.ToLowerInvariant() is "currentcolor" or "transparent",
            { Kind: CssTokenizer.TokenKind.Function } origin when string.Equals(origin.Value, "var", StringComparison.OrdinalIgnoreCase) =>
                arguments.Count > 2 && arguments[2].Kind == CssTokenizer.TokenKind.Ident &&
                (arguments[2].Value.StartsWith("--color-", StringComparison.Ordinal) || arguments[2].Value.StartsWith("--setting-", StringComparison.Ordinal)),
            _ => false,
        };
    }

    private static string GetBlockOpener(IList<CssTokenizer.Token> prelude, Stack<string> openers)
    {
        var first = prelude.FirstOrDefault(x => x.Kind != CssTokenizer.TokenKind.Whitespace);
        if (first.Kind == CssTokenizer.TokenKind.AtKeyword)
        {
            return first.Value.ToLowerInvariant();
        }

        return openers.Count > 0 && openers.Peek() == KeyframesAtRule ? KeyframeBlock : StyleRuleBlock;
    }

    private static string DescribeBlock(string opener) => opener == StyleRuleBlock ? "a style rule" : "@" + opener;

    private static string WriteCss(IEnumerable<CssTokenizer.Token> tokens) =>
        string.Concat(tokens.Select(x => x.Kind switch
        {
            CssTokenizer.TokenKind.Function => x.Value + "(",
            CssTokenizer.TokenKind.AtKeyword => "@" + x.Value,
            CssTokenizer.TokenKind.Hash => "#" + x.Value,
            CssTokenizer.TokenKind.String => $"\"{x.Value}\"",
            CssTokenizer.TokenKind.Url => $"url({x.Value})",
            _ => x.Value,
        })).Trim();

    private static bool IsLateValueFunction(string name) => LateValueFunctions.Contains(name) || name.StartsWith("--", StringComparison.Ordinal);

    // a string inside a late value function stands, once the function is resolved, where the function does, as in image-set(if(else: "a.png"))
    private static bool IsImageArgument(IEnumerable<CssTokenizer.Token> blocks) =>
        blocks.FirstOrDefault(x => x.Kind != CssTokenizer.TokenKind.Function || !IsLateValueFunction(x.Value)) is { Kind: CssTokenizer.TokenKind.Function } function &&
        ImageFunctions.Contains(function.Value);

    private static List<CssTokenizer.Token> GetOwnTokens(List<CssTokenizer.Token> value)
    {
        var own = new List<CssTokenizer.Token>();
        var depth = 0;
        foreach (var token in value)
        {
            if (depth == 0)
            {
                own.Add(token);
            }

            depth += GetDepthChange(token);
        }

        return own;
    }

    private static bool RemovesFocusRing(string name, List<CssTokenizer.Token> own) => name switch
    {
        "outline" => own.Exists(x => IsNone(x) || IsZero(x)),
        "outline-style" => own.Exists(IsNone),
        "outline-width" => own.Exists(IsZero),
        _ => false,
    };

    // a CSS-wide keyword gives the outline its initial style, none, or its parent's; the initial width is medium
    private static bool IsHidingKeyword(string name, string keyword) => (name, keyword.ToLowerInvariant()) switch
    {
        ("outline" or "outline-style" or "all", "initial" or "unset" or "inherit") => true,
        ("outline-width", "inherit") => true,
        _ => false,
    };

    /// <returns>The first function the property does not take; <c>null</c> for none.</returns>
    private static string GetFocusRingFunction(string name, List<CssTokenizer.Token> value, IReadOnlySet<string> colorSettings) => name switch
    {
        "outline" or "outline-color" => value
            .Where((x, i) => x.Kind == CssTokenizer.TokenKind.Function && !ColorFunctions.Contains(x.Value) &&
                !string.Equals(x.Value, "color-mix", StringComparison.OrdinalIgnoreCase) && !IsColorVariable(value, i, colorSettings))
            .Select(x => x.Value)
            .FirstOrDefault(),
        "outline-style" or "outline-width" or "all" => value.Find(x => x.Kind == CssTokenizer.TokenKind.Function).Value,
        _ => null,
    };

    private static bool IsColorVariable(List<CssTokenizer.Token> value, int function, IReadOnlySet<string> colorSettings) =>
        string.Equals(value[function].Value, "var", StringComparison.OrdinalIgnoreCase) &&
        value.Skip(function + 1).Where(x => x.Kind != CssTokenizer.TokenKind.Whitespace).Take(2).ToList() is
            [{ Kind: CssTokenizer.TokenKind.Ident } variable, { Kind: CssTokenizer.TokenKind.CloseParen }] &&
        IsPaletteVariable(variable.Value, colorSettings);

    // the theme sets each --color-… variable to a color of its palette, and the storefront a color-token setting's to one of them, and the
    // type's CSS sets neither; any other variable, a number setting's included, may hold anything
    private static bool IsPaletteVariable(string name, IReadOnlySet<string> colorSettings) =>
        name.StartsWith("--color-", StringComparison.Ordinal) || colorSettings.Contains(name);

    // the storefront's ring reads its --vc-focus-ring-… variables, and an outline may read a palette variable
    private static bool IsFocusRingVariable(string name, IReadOnlySet<string> colorSettings) =>
        name.StartsWith("--vc-focus-ring-", StringComparison.Ordinal) || IsPaletteVariable(name, colorSettings);

    private static bool IsNone(CssTokenizer.Token token) =>
        token.Kind == CssTokenizer.TokenKind.Ident && string.Equals(token.Value, "none", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransparent(CssTokenizer.Token token) =>
        token.Kind == CssTokenizer.TokenKind.Ident && string.Equals(token.Value, "transparent", StringComparison.OrdinalIgnoreCase);

    private static bool IsZero(CssTokenizer.Token token)
    {
        if (token.Kind is not (CssTokenizer.TokenKind.Number or CssTokenizer.TokenKind.Dimension))
        {
            return false;
        }

        var number = NumberPattern.Match(token.Value).Value;

        return decimal.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var zero) && zero == 0;
    }

    private static bool HasReducedMotionQuery(IList<CssTokenizer.Token> tokens)
    {
        var meaningful = tokens.Where(x => x.Kind != CssTokenizer.TokenKind.Whitespace).ToList();

        return meaningful.Zip(meaningful.Skip(1), meaningful.Skip(2)).Any(x =>
            x.First.Kind == CssTokenizer.TokenKind.Ident && string.Equals(x.First.Value, "prefers-reduced-motion", StringComparison.OrdinalIgnoreCase) &&
            x.Second.Kind == CssTokenizer.TokenKind.Colon &&
            x.Third.Kind == CssTokenizer.TokenKind.Ident && ReducedMotionValues.Contains(x.Third.Value));
    }

    /// <summary>
    /// A declaration is a name and a colon whose value ends at a ';' or a '}'; a name, a colon and a block of its own is a nested rule, such as
    /// a:hover { }.
    /// </summary>
    /// <returns>The value; <c>null</c> for no declaration.</returns>
    private static List<CssTokenizer.Token> ReadDeclarationValue(IList<CssTokenizer.Token> tokens, int index)
    {
        var colon = index + 1;
        while (colon < tokens.Count && tokens[colon].Kind == CssTokenizer.TokenKind.Whitespace)
        {
            colon++;
        }

        if (colon >= tokens.Count || tokens[colon].Kind != CssTokenizer.TokenKind.Colon)
        {
            return null;
        }

        var value = new List<CssTokenizer.Token>();
        var depth = 0;
        for (var i = colon + 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (depth == 0 && token.Kind is CssTokenizer.TokenKind.Semicolon or CssTokenizer.TokenKind.CloseCurly)
            {
                break;
            }

            if (depth == 0 && token.Kind == CssTokenizer.TokenKind.OpenCurly)
            {
                return null;
            }

            depth += GetDepthChange(token);
            value.Add(token);
        }

        return value;
    }

    private static int GetDepthChange(CssTokenizer.Token token) => token.Kind switch
    {
        CssTokenizer.TokenKind.OpenCurly or CssTokenizer.TokenKind.OpenParen or CssTokenizer.TokenKind.OpenSquare or CssTokenizer.TokenKind.Function => 1,
        CssTokenizer.TokenKind.CloseCurly or CssTokenizer.TokenKind.CloseParen or CssTokenizer.TokenKind.CloseSquare => -1,
        _ => 0,
    };

    private static char GetOpener(CssTokenizer.TokenKind closer) => closer switch
    {
        CssTokenizer.TokenKind.CloseCurly => '{',
        CssTokenizer.TokenKind.CloseParen => '(',
        _ => '[',
    };

    /// <param name="Function">The late value function the shorthand's value holds, such as var.</param>
    protected sealed record LateShorthand(string Name, string Function, IReadOnlySet<string> Parts, bool Important);

    /// <summary>What the checks of the CSS's tokens found, before they are reported once each.</summary>
    protected sealed class CssFindings
    {
        public List<string> Errors { get; } = [];

        /// <summary>The warnings of a side of the page that stays when the page reads right to left.</summary>
        public List<string> Directions { get; } = [];

        public List<string> ColorProperties { get; } = [];

        public bool Animates { get; set; }
    }
}
