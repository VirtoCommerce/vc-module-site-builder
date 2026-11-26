namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>
/// The storefront's @scope keeps inside the section only the element a rule styles: every other part of its selector matches against the whole page,
/// and a conditional image can tell what it found. So :scope or &amp; comes only first, a :has() looks at no sibling, an :nth-child(… of …) counts no
/// sibling of the section's root, the arguments matched against the whole page are compound selectors, and no selector names :root, or html or
/// body as a type. The storefront drops such a rule.
/// </summary>
public static class CustomSectionCssSelector
{
    private const string Outside = "can match outside the section: ";
    private const string Document = "reaches :root, html or body, outside the section";
    private const string HasPseudo = "has";
    private const string ScopePseudo = "scope";
    private const string RootPseudo = "root";
    private const string NthOfKeyword = "of";

    /// <summary>Their arguments match against the whole page, so they take compound selectors only, as the S of :nth-child(… of S) does.</summary>
    public static IReadOnlySet<string> CompoundArgumentPseudos { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "is", "where", "not", "matches", "any", "-webkit-any", "-moz-any", "host", "host-context", "slotted", "cue", "current", "past", "future",
    };

    /// <summary>Their arguments hold no selector: :nth-child() and :nth-last-child() without of among them.</summary>
    public static IReadOnlySet<string> PlainArgumentPseudos { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "nth-child", "nth-last-child", "nth-of-type", "nth-last-of-type", "nth-col", "nth-last-col", "lang", "dir", "state", "part", "highlight",
        "picker", "view-transition-group", "view-transition-image-pair", "view-transition-old", "view-transition-new", "active-view-transition-type",
    };

    private static readonly HashSet<string> NthChildPseudos = new(StringComparer.OrdinalIgnoreCase) { "nth-child", "nth-last-child" };

    private static readonly HashSet<string> DocumentTypes = new(StringComparer.OrdinalIgnoreCase) { "html", "body" };

    /// <param name="selectors">A style rule's prelude: its selector list, as tokens.</param>
    /// <returns>What makes the list match outside the section, to follow the selector in an error; <c>null</c> for nothing.</returns>
    public static string GetOutsideMatch(IList<CssTokenizer.Token> selectors) => new SelectorReader(ReadSelectorTokens(selectors)).Read();

    /// <summary>
    /// The tokens with an attribute selector as its '[' alone, so that its value is no syntax, and with a whitespace token only where it is a
    /// descendant combinator.
    /// </summary>
    private static List<CssTokenizer.Token> ReadSelectorTokens(IList<CssTokenizer.Token> prelude)
    {
        var result = new List<CssTokenizer.Token>();
        var spaced = false;
        for (var i = 0; i < prelude.Count; i++)
        {
            var token = prelude[i];
            if (token.Kind == CssTokenizer.TokenKind.Whitespace)
            {
                spaced = true;
                continue;
            }

            if (spaced && result.Count > 0 && EndsCompound(result[^1]) && StartsCompound(token))
            {
                result.Add(new CssTokenizer.Token(CssTokenizer.TokenKind.Whitespace, " "));
            }

            spaced = false;
            result.Add(token);
            if (token.Kind == CssTokenizer.TokenKind.OpenSquare)
            {
                i = SkipAttribute(prelude, i);
            }
        }

        return result;
    }

    private static int SkipAttribute(IList<CssTokenizer.Token> prelude, int start)
    {
        var depth = 0;
        for (var i = start; i < prelude.Count; i++)
        {
            depth += prelude[i].Kind switch { CssTokenizer.TokenKind.OpenSquare => 1, CssTokenizer.TokenKind.CloseSquare => -1, _ => 0 };
            if (depth == 0)
            {
                return i;
            }
        }

        return prelude.Count - 1;
    }

    private static bool EndsCompound(CssTokenizer.Token token) =>
        token.Kind is not (CssTokenizer.TokenKind.Comma or CssTokenizer.TokenKind.Function or CssTokenizer.TokenKind.OpenParen or CssTokenizer.TokenKind.Colon) &&
        !IsCombinator(token);

    private static bool StartsCompound(CssTokenizer.Token token) =>
        token.Kind is not (CssTokenizer.TokenKind.Comma or CssTokenizer.TokenKind.CloseParen) && !IsCombinator(token);

    private static bool IsCombinator(CssTokenizer.Token token) => token.Kind == CssTokenizer.TokenKind.Delim && token.Value is ">" or "+" or "~";

    /// <param name="display">The pseudo-class as an error names it, such as :is().</param>
    /// <param name="isHas">A :has(), whose argument may hold combinators; any other takes compound selectors only.</param>
    private sealed class Group(string display, bool isHas)
    {
        public string Display { get; } = display;

        public bool IsHas { get; } = isHas;

        /// <summary>Nothing read yet of the argument's current selector.</summary>
        public bool AtStart { get; set; } = true;
    }

    private sealed class SelectorReader(List<CssTokenizer.Token> tokens)
    {
        private readonly Stack<Group> _groups = new();
        private int _index;

        // of the current top-level selector of the list
        private bool _hasContent;
        private bool _anchored;
        private bool _inFirstCompound = true;

        public string Read()
        {
            for (; _index < tokens.Count; _index++)
            {
                if (tokens[_index].Kind == CssTokenizer.TokenKind.Comma)
                {
                    StartSelector();
                    continue;
                }

                var group = _groups.Count > 0 ? _groups.Peek() : null;
                if (ReadToken(tokens[_index]) is { } outside)
                {
                    return outside;
                }

                _hasContent = true;
                if (group != null)
                {
                    group.AtStart = false;
                }
            }

            return _groups.Count == 0 ? null : "has a '(' without its pair";
        }

        private string ReadToken(CssTokenizer.Token token) => token.Kind switch
        {
            CssTokenizer.TokenKind.Colon => ReadPseudo(),
            CssTokenizer.TokenKind.Whitespace => ReadCombinator(token.Value),
            CssTokenizer.TokenKind.Delim when token.Value == "&" => Anchor(),
            CssTokenizer.TokenKind.Delim when IsCombinator(token) => ReadCombinator(token.Value),
            CssTokenizer.TokenKind.Ident when IsDocumentType(token) => Document,
            CssTokenizer.TokenKind.Function => $"has '{token.Value}(' outside a pseudo-class",
            CssTokenizer.TokenKind.OpenParen => "has a '(' outside a pseudo-class",
            CssTokenizer.TokenKind.CloseParen => CloseGroup(),
            _ => null,
        };

        // a name after a '.' is a class; any other the reader reaches is a type, *|body included
        private bool IsDocumentType(CssTokenizer.Token token) =>
            DocumentTypes.Contains(token.Value) && !(_index > 0 && tokens[_index - 1] is { Kind: CssTokenizer.TokenKind.Delim, Value: "." });

        private string ReadPseudo()
        {
            var prefix = ":";
            var next = _index + 1;
            if (next < tokens.Count && tokens[next].Kind == CssTokenizer.TokenKind.Colon)
            {
                prefix = "::";
                next++;
            }

            var token = next < tokens.Count ? tokens[next] : default;
            if (token.Kind is not (CssTokenizer.TokenKind.Ident or CssTokenizer.TokenKind.Function))
            {
                return null;
            }

            _index = next;
            return token.Kind == CssTokenizer.TokenKind.Ident ? ReadPseudoName(token.Value) : ReadPseudoFunction(prefix, token.Value);
        }

        private string ReadPseudoName(string name)
        {
            if (string.Equals(name, RootPseudo, StringComparison.OrdinalIgnoreCase))
            {
                return Document;
            }

            return string.Equals(name, ScopePseudo, StringComparison.OrdinalIgnoreCase) ? Anchor() : null;
        }

        private string ReadPseudoFunction(string prefix, string name)
        {
            var pseudo = prefix + name.ToLowerInvariant();
            var isHas = string.Equals(name, HasPseudo, StringComparison.OrdinalIgnoreCase);
            if (isHas || CompoundArgumentPseudos.Contains(name))
            {
                _groups.Push(new Group(pseudo + "()", isHas));
                return null;
            }

            if (NthChildPseudos.Contains(name) && FindNthOf() is { } of)
            {
                return ReadNthOf(pseudo, of);
            }

            if (PlainArgumentPseudos.Contains(name))
            {
                return SkipArgument() ? null : "has a '(' without its pair";
            }

            return $"{Outside}{pseudo}() is not a {(prefix == "::" ? "pseudo-element" : "pseudo-class")} the storefront takes";
        }

        private string ReadNthOf(string pseudo, int of)
        {
            // the root's siblings are outside the section
            if (_anchored && _inFirstCompound)
            {
                return $"{Outside}{pseudo}(… of …) on :scope or & counts the section's siblings";
            }

            _groups.Push(new Group(pseudo + "(… of …)", isHas: false));
            _index = of + 1 < tokens.Count && tokens[of + 1].Kind == CssTokenizer.TokenKind.Whitespace ? of + 1 : of;
            return null;
        }

        /// <returns>The index of the of in :nth-child(An+B of S); <c>null</c> for none.</returns>
        private int? FindNthOf()
        {
            for (var i = _index + 1; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Kind is CssTokenizer.TokenKind.Function or CssTokenizer.TokenKind.OpenParen or CssTokenizer.TokenKind.CloseParen)
                {
                    return null;
                }

                if (token.Kind == CssTokenizer.TokenKind.Ident && string.Equals(token.Value, NthOfKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return null;
        }

        private bool SkipArgument()
        {
            var depth = 0;
            for (var i = _index + 1; i < tokens.Count; i++)
            {
                switch (tokens[i].Kind)
                {
                    case CssTokenizer.TokenKind.Function or CssTokenizer.TokenKind.OpenParen:
                        depth++;
                        break;
                    case CssTokenizer.TokenKind.CloseParen when depth == 0:
                        _index = i;
                        return true;
                    case CssTokenizer.TokenKind.CloseParen:
                        depth--;
                        break;
                }
            }

            return false;
        }

        private string ReadCombinator(string combinator)
        {
            if (_groups.Count == 0)
            {
                _inFirstCompound &= !_hasContent;
                return null;
            }

            var group = _groups.Peek();
            if (!group.IsHas)
            {
                return $"{Outside}{group.Display} takes only compound selectors, with no combinator";
            }

            return group.AtStart && combinator is "+" or "~" ? $"{Outside}a selector in :has() starts with no + or ~" : null;
        }

        private string CloseGroup() => _groups.TryPop(out _) ? null : "has a ')' without its pair";

        private string Anchor()
        {
            if (_groups.Count > 0 || _hasContent)
            {
                return $"{Outside}:scope or & comes only once, at the start of a selector";
            }

            _anchored = true;
            return null;
        }

        private void StartSelector()
        {
            if (_groups.Count > 0)
            {
                _groups.Peek().AtStart = true;
                return;
            }

            _hasContent = false;
            _anchored = false;
            _inFirstCompound = true;
        }
    }
}
