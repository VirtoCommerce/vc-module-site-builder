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
    public static IReadOnlySet<string> LateValueFunctions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "var", "attr", "env", "if" };

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

    private static readonly HashSet<string> MotionProperties = new(StringComparer.OrdinalIgnoreCase) { "animation", "animation-name", "transition" };

    private static readonly HashSet<string> ReducedMotionValues = new(StringComparer.OrdinalIgnoreCase) { "reduce", "no-preference" };

    // the properties whose value may hold a color by its name; anywhere else a name such as tan may be anything
    private static readonly Regex ColorPropertyPattern = new("color|^background|^border|^outline|^column-rule|^text-decoration|shadow$|^fill$|^stroke$|^filter$",
        RegexOptions.None, TimeSpan.FromSeconds(1));

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
        var inAtRulePrelude = false;
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case CssTokenizer.TokenKind.BadString:
                    return "the CSS has a string that a line break cuts off";
                case CssTokenizer.TokenKind.BadUrl:
                    return "the CSS has a url( whose URL a quote, a parenthesis or a space breaks: quote the URL";
                case CssTokenizer.TokenKind.AtKeyword:
                    inAtRulePrelude = true;
                    break;
                case CssTokenizer.TokenKind.Semicolon:
                    inAtRulePrelude = false;
                    break;
                case CssTokenizer.TokenKind.OpenCurly:
                    blocks.Push((CssTokenizer.TokenKind.CloseCurly, inAtRulePrelude));
                    atRules += inAtRulePrelude ? 1 : 0;
                    inAtRulePrelude = false;
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
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var block = blocks.Count > 0 ? blocks.Peek() : default;
            switch (token.Kind)
            {
                case CssTokenizer.TokenKind.Function when string.Equals(token.Value, "dir", StringComparison.OrdinalIgnoreCase):
                    preludeNamesDirection = true;
                    break;
                case CssTokenizer.TokenKind.Semicolon or CssTokenizer.TokenKind.CloseCurly:
                    preludeNamesDirection = false;
                    break;
                case CssTokenizer.TokenKind.AtKeyword:
                    ValidateCssAtRule(check, tokens, i, findings);
                    break;
                case CssTokenizer.TokenKind.Url:
                    AddCssUrlError(token.Value, findings);
                    break;
                case CssTokenizer.TokenKind.String when imageFunctions.Count > 0:
                    AddCssUrlError(token.Value, findings);
                    break;
                case CssTokenizer.TokenKind.Function when RefusedFunctions.TryGetValue(token.Value, out var refused):
                    findings.Errors.Add($"the CSS has {token.Value.ToLowerInvariant()}(), which {refused}");
                    break;
                case CssTokenizer.TokenKind.Function when IsLateValueFunction(token.Value) && imageFunctions.Count > 0:
                    findings.Errors.Add($"the CSS has {token.Value.ToLowerInvariant()}() inside {imageFunctions.Peek()}(), whose URL is then not known on save");
                    break;
                case CssTokenizer.TokenKind.Ident when blocks.Count == 0 && atDeclarationStart && ReadDeclarationValue(tokens, i) != null:
                    // a browser drops it, with the rule after it
                    findings.Errors.Add($"the CSS has the declaration '{token.Value}' outside any rule: write it in :scope {{ }}");
                    break;
                case CssTokenizer.TokenKind.Ident when block.Kind == CssTokenizer.TokenKind.OpenCurly && atDeclarationStart:
                    ValidateCssDeclaration(tokens, i, findings, inDirectionRule: directionRules.Count > 0 && directionRules.Peek());
                    break;
            }

            if (token.Kind != CssTokenizer.TokenKind.Whitespace)
            {
                atDeclarationStart = token.Kind is CssTokenizer.TokenKind.OpenCurly or CssTokenizer.TokenKind.Semicolon or CssTokenizer.TokenKind.CloseCurly;
            }

            if (token.Kind == CssTokenizer.TokenKind.OpenCurly)
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
            }

            if (token.Kind is CssTokenizer.TokenKind.OpenCurly or CssTokenizer.TokenKind.CloseCurly or CssTokenizer.TokenKind.Semicolon)
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

                if (token.Kind == CssTokenizer.TokenKind.CloseCurly)
                {
                    directionRules.Pop();
                    openers.Pop();
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
    /// A rule's selector may not name :root, html or body: the storefront drops such a rule, as html or body short of the subject reads the
    /// page outside the section. Nor may any other part of it match outside the section (<see cref="CustomSectionCssSelector"/>).
    /// </summary>
    protected virtual void ValidateCssSelector(IList<CssTokenizer.Token> prelude, CssFindings findings)
    {
        var first = prelude.FirstOrDefault(x => x.Kind != CssTokenizer.TokenKind.Whitespace);
        if (first.Kind is CssTokenizer.TokenKind.None or CssTokenizer.TokenKind.AtKeyword)
        {
            return;
        }

        var attributes = 0;
        var previous = default(CssTokenizer.Token);
        foreach (var token in prelude.Where(x => x.Kind != CssTokenizer.TokenKind.Whitespace))
        {
            attributes += token.Kind switch { CssTokenizer.TokenKind.OpenSquare => 1, CssTokenizer.TokenKind.CloseSquare => -1, _ => 0 };
            var isDocument = attributes == 0 && token.Kind == CssTokenizer.TokenKind.Ident && (previous.Kind == CssTokenizer.TokenKind.Colon
                ? string.Equals(token.Value, "root", StringComparison.OrdinalIgnoreCase)
                : !(previous.Kind == CssTokenizer.TokenKind.Delim && previous.Value is "." or "|") && token.Value.ToLowerInvariant() is "html" or "body");
            if (isDocument)
            {
                findings.Errors.Add($"the CSS selector '{Quote(WriteCss(prelude))}' reaches :root, html or body, outside the section");
                return;
            }

            previous = token;
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
        else if (RemovesFocusRing(name, value))
        {
            findings.Errors.Add($"the CSS sets {name.ToLowerInvariant()} to none or 0, which hides where the keyboard is on the page");
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

    private static bool RemovesFocusRing(string name, List<CssTokenizer.Token> value) => name.ToLowerInvariant() switch
    {
        "outline" => value.Exists(x => IsNone(x) || IsZero(x)),
        "outline-style" => value.Exists(IsNone),
        "outline-width" => value.Exists(IsZero),
        _ => false,
    };

    private static bool IsNone(CssTokenizer.Token token) =>
        token.Kind == CssTokenizer.TokenKind.Ident && string.Equals(token.Value, "none", StringComparison.OrdinalIgnoreCase);

    private static bool IsZero(CssTokenizer.Token token)
    {
        if (token.Kind is not (CssTokenizer.TokenKind.Number or CssTokenizer.TokenKind.Dimension))
        {
            return false;
        }

        var number = new string(token.Value.TakeWhile(x => char.IsAsciiDigit(x) || x is '.' or '+' or '-').ToArray());

        return decimal.TryParse(number, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var zero) && zero == 0;
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

            depth += token.Kind switch
            {
                CssTokenizer.TokenKind.OpenCurly or CssTokenizer.TokenKind.OpenParen or CssTokenizer.TokenKind.OpenSquare or CssTokenizer.TokenKind.Function => 1,
                CssTokenizer.TokenKind.CloseCurly or CssTokenizer.TokenKind.CloseParen or CssTokenizer.TokenKind.CloseSquare => -1,
                _ => 0,
            };
            value.Add(token);
        }

        return value;
    }

    private static char GetOpener(CssTokenizer.TokenKind closer) => closer switch
    {
        CssTokenizer.TokenKind.CloseCurly => '{',
        CssTokenizer.TokenKind.CloseParen => '(',
        _ => '[',
    };

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
