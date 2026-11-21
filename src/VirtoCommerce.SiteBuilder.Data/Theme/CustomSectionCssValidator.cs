using System.Globalization;
using System.Text.RegularExpressions;
using static VirtoCommerce.SiteBuilder.Data.Theme.CustomSectionText;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>A custom section type's CSS, read from its tokens as a browser reads it: a comment in a string, an escape or a quote in a URL included.</summary>
public class CustomSectionCssValidator(CustomSectionUrlPolicy urlPolicy)
{
    private const int MaxSamples = 3;
    private const string KeyframesAtRule = "keyframes";

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
        if (GetCssStructureError(check, tokens) is { } error)
        {
            check.AddError(error);
            return;
        }

        ValidateCssTokens(check, tokens);
        AddColorWarning(check);
    }

    /// <returns>The error of a token no browser reads as written, of a block without its pair or nested too deep; <c>null</c> for none.</returns>
    protected virtual string GetCssStructureError(CustomSectionCheck check, IList<CssTokenizer.Token> tokens)
    {
        // each open block with the token that closes it, and whether an at-rule opened it
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

            if (blocks.Count > check.Rules.MaxCssDepth)
            {
                return string.Create(CultureInfo.InvariantCulture, $"the CSS nests blocks deeper than {check.Rules.MaxCssDepth}");
            }

            if (atRules > check.Rules.MaxAtRuleDepth)
            {
                return string.Create(CultureInfo.InvariantCulture, $"the CSS nests at-rules deeper than {check.Rules.MaxAtRuleDepth}");
            }
        }

        return blocks.Count == 0 ? null : $"the CSS has a '{GetOpener(blocks.Peek().Closer)}' without its pair";
    }

    /// <summary>The at-rules, the URLs, and the declarations that run script or take the section out of the page, custom properties included.</summary>
    protected virtual void ValidateCssTokens(CustomSectionCheck check, IList<CssTokenizer.Token> tokens)
    {
        var errors = new List<string>();
        var directions = new List<string>();
        // the function or the block each token is in, the functions that load an image it is in, and whether each rule's block is for one
        // direction, as :dir(rtl) { } is
        var blocks = new Stack<CssTokenizer.Token>();
        var imageFunctions = new Stack<string>();
        var directionRules = new Stack<bool>();
        var preludeNamesDirection = false;
        // a declaration starts the CSS or a block, or follows a ';' or a nested rule; an ident anywhere else is part of a value or a selector
        var atDeclarationStart = true;
        // the tokens since the last block or ';': a rule's selector, or an at-rule's prelude, once a '{' follows
        var prelude = new List<CssTokenizer.Token>();
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
                    ValidateCssAtRule(check, tokens, i, errors);
                    break;
                case CssTokenizer.TokenKind.Url:
                    AddCssUrlError(check, token.Value, errors);
                    break;
                case CssTokenizer.TokenKind.String when imageFunctions.Count > 0:
                    AddCssUrlError(check, token.Value, errors);
                    break;
                case CssTokenizer.TokenKind.Function when string.Equals(token.Value, "expression", StringComparison.OrdinalIgnoreCase):
                    errors.Add("the CSS has expression(), which runs script");
                    break;
                case CssTokenizer.TokenKind.Function when IsLateValueFunction(check, token.Value) && imageFunctions.Count > 0:
                    errors.Add($"the CSS has {token.Value.ToLowerInvariant()}() inside {imageFunctions.Peek()}(), whose URL is then not known on save");
                    break;
                case CssTokenizer.TokenKind.Ident when blocks.Count == 0 && atDeclarationStart && IsDeclaration(tokens, i):
                    // the storefront's @scope would apply it to the section's root, where no check of a rule reaches it
                    errors.Add($"the CSS has the declaration '{token.Value}' outside any rule: write it in :scope {{ }}");
                    break;
                case CssTokenizer.TokenKind.Ident when block.Kind == CssTokenizer.TokenKind.OpenCurly && atDeclarationStart:
                    ValidateCssDeclaration(check, tokens, i, errors, directionRules.Count > 0 && directionRules.Peek() ? null : directions);
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
                ValidateCssSelector(prelude, errors);
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
                if (token.Kind == CssTokenizer.TokenKind.Function && check.Rules.ImageFunctions.Contains(token.Value, StringComparer.OrdinalIgnoreCase))
                {
                    imageFunctions.Push(token.Value.ToLowerInvariant());
                }
            }
            else if (token.Kind is CssTokenizer.TokenKind.CloseCurly or CssTokenizer.TokenKind.CloseParen or CssTokenizer.TokenKind.CloseSquare)
            {
                var closed = blocks.Pop();
                if (closed.Kind == CssTokenizer.TokenKind.Function && check.Rules.ImageFunctions.Contains(closed.Value, StringComparer.OrdinalIgnoreCase))
                {
                    imageFunctions.Pop();
                }

                if (token.Kind == CssTokenizer.TokenKind.CloseCurly)
                {
                    directionRules.Pop();
                }
            }
        }

        foreach (var error in errors.Distinct(StringComparer.Ordinal))
        {
            check.AddError(error);
        }

        foreach (var direction in directions.Distinct(StringComparer.Ordinal))
        {
            check.AddWarning(direction);
        }

        if (check.Animates && !HasReducedMotionQuery(tokens))
        {
            check.AddWarning("the CSS animates with no @media (prefers-reduced-motion: reduce) block, so it moves for people who ask for less motion");
        }
    }

    /// <summary>
    /// A rule's selector may not name :root, html or body. The storefront scopes the CSS to the section, where they match nothing, but a
    /// browser without @scope would apply them to the page.
    /// </summary>
    protected virtual void ValidateCssSelector(IList<CssTokenizer.Token> prelude, IList<string> errors)
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
                errors.Add($"the CSS selector '{Quote(WriteCss(prelude))}' reaches :root, html or body, outside the section");
                return;
            }

            previous = token;
        }
    }

    protected virtual void ValidateCssAtRule(CustomSectionCheck check, IList<CssTokenizer.Token> tokens, int index, IList<string> errors)
    {
        var name = tokens[index].Value;
        if (!check.Rules.AtRules.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"the CSS has @{name}, and a custom section's CSS has only {JoinAnd(check.Rules.AtRules.Select(x => "@" + x))}");
            return;
        }

        if (!string.Equals(name, KeyframesAtRule, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // keyframes names are not scoped, and a type's name may start another's, so the type's own start with the type and '__'
        var prefix = check.Type + check.Rules.KeyframesSeparator;
        var keyframes = tokens.Skip(index + 1).FirstOrDefault(x => x.Kind != CssTokenizer.TokenKind.Whitespace);
        if (keyframes.Kind is not (CssTokenizer.TokenKind.Ident or CssTokenizer.TokenKind.String))
        {
            errors.Add("the CSS has @keyframes without a name");
        }
        else if (keyframes.Value.Length <= prefix.Length || !keyframes.Value.StartsWith(prefix, StringComparison.Ordinal))
        {
            errors.Add($"the CSS names @keyframes '{Quote(keyframes.Value)}', which does not start with '{prefix}': keyframes names are not scoped");
        }
    }

    /// <summary>A declaration is a name and a colon in a block; a name, a colon and a block of its own is a nested rule, such as a:hover { }.</summary>
    /// <param name="directions">Takes a warning for a side of the page that stays when the page reads right to left; <c>null</c> in a rule for one direction.</param>
    protected virtual void ValidateCssDeclaration(CustomSectionCheck check, IList<CssTokenizer.Token> tokens, int index, IList<string> errors,
        IList<string> directions)
    {
        var colon = index + 1;
        while (colon < tokens.Count && tokens[colon].Kind == CssTokenizer.TokenKind.Whitespace)
        {
            colon++;
        }

        if (colon >= tokens.Count || tokens[colon].Kind != CssTokenizer.TokenKind.Colon)
        {
            return;
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
                return;
            }

            depth += token.Kind switch
            {
                CssTokenizer.TokenKind.OpenCurly or CssTokenizer.TokenKind.OpenParen or CssTokenizer.TokenKind.OpenSquare or CssTokenizer.TokenKind.Function => 1,
                CssTokenizer.TokenKind.CloseCurly or CssTokenizer.TokenKind.CloseParen or CssTokenizer.TokenKind.CloseSquare => -1,
                _ => 0,
            };
            value.Add(token);
        }

        var name = tokens[index].Value;
        check.Animates |= MotionProperties.Contains(name);
        if (HasLiteralColor(name, value))
        {
            check.ColorProperties.Add(name.ToLowerInvariant());
        }

        if (directions != null && GetLogicalDirection(check, name, value) is { } logical)
        {
            directions.Add($"the CSS sets {logical.Physical}, which stays on that side when the page reads right to left: use {logical.Logical}");
        }

        if (check.Rules.ScriptProperties.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"the CSS sets {name.ToLowerInvariant()}, which runs script");
        }
        else if (RemovesFocusRing(name, value))
        {
            errors.Add($"the CSS sets {name.ToLowerInvariant()} to none or 0, which hides where the keyboard is on the page");
        }
        else if (string.Equals(name, "position", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Exists(x => x.Kind == CssTokenizer.TokenKind.Ident && string.Equals(x.Value, "fixed", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("the CSS has position: fixed, which puts the section over the page");
            }
            else if (value.Find(x => x.Kind == CssTokenizer.TokenKind.Function && IsLateValueFunction(check, x.Value)) is { Value: { } function })
            {
                errors.Add($"the CSS sets position with {function.ToLowerInvariant()}(), which could make it fixed");
            }
        }
    }

    protected virtual void AddColorWarning(CustomSectionCheck check)
    {
        if (check.ColorProperties.Count > 0)
        {
            var properties = string.Join(", ", check.ColorProperties.Distinct(StringComparer.Ordinal).Take(MaxSamples));
            check.AddWarning($"the CSS sets literal colors ({properties}), which the palette does not change: use var(--color-…) or a color-token setting's variable");
        }
    }

    protected virtual void AddCssUrlError(CustomSectionCheck check, string url, IList<string> errors)
    {
        if (!urlPolicy.IsAssetUrl(url) && !urlPolicy.IsImageDataUrl(url))
        {
            errors.Add($"the CSS loads '{Quote(url)}', which is not a URL of the store's asset library or a data:image/ URL");
        }
    }

    // a value for one side is looked for as "text-align: left"
    private static (string Physical, string Logical)? GetLogicalDirection(CustomSectionCheck check, string name, List<CssTokenizer.Token> value)
    {
        var property = name.ToLowerInvariant();
        if (check.Rules.LogicalProperties.TryGetValue(property, out var logicalProperty))
        {
            return (property, logicalProperty);
        }

        var side = value.Find(x => x.Kind == CssTokenizer.TokenKind.Ident && x.Value.ToLowerInvariant() is "left" or "right").Value?.ToLowerInvariant();
        var physical = $"{property}: {side}";

        return side != null && check.Rules.LogicalProperties.TryGetValue(physical, out var logical) ? (physical, logical) : null;
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

    private static bool IsLateValueFunction(CustomSectionCheck check, string name) =>
        check.Rules.LateValueFunctions.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith("--", StringComparison.Ordinal);

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

    // a name and a colon that end at a ';' or a '}', not at a '{' as the selector a:hover { } does
    private static bool IsDeclaration(IList<CssTokenizer.Token> tokens, int index)
    {
        var next = index + 1;
        while (next < tokens.Count && tokens[next].Kind == CssTokenizer.TokenKind.Whitespace)
        {
            next++;
        }

        if (next >= tokens.Count || tokens[next].Kind != CssTokenizer.TokenKind.Colon)
        {
            return false;
        }

        var depth = 0;
        for (var i = next + 1; i < tokens.Count; i++)
        {
            switch (tokens[i].Kind)
            {
                case CssTokenizer.TokenKind.OpenCurly when depth == 0:
                    return false;
                case CssTokenizer.TokenKind.Semicolon or CssTokenizer.TokenKind.CloseCurly when depth == 0:
                    return true;
                case CssTokenizer.TokenKind.OpenParen or CssTokenizer.TokenKind.OpenSquare or CssTokenizer.TokenKind.Function:
                    depth++;
                    break;
                case CssTokenizer.TokenKind.CloseParen or CssTokenizer.TokenKind.CloseSquare:
                    depth--;
                    break;
            }
        }

        return true;
    }

    private static char GetOpener(CssTokenizer.TokenKind closer) => closer switch
    {
        CssTokenizer.TokenKind.CloseCurly => '{',
        CssTokenizer.TokenKind.CloseParen => '(',
        _ => '[',
    };
}
