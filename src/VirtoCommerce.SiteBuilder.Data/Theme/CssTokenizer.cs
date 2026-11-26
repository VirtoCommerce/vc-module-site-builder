using System.Globalization;
using System.Text;

namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>
/// Reads CSS into tokens as a browser does (CSS Syntax Level 3, "Tokenization"), so that a check of the tokens sees what a browser applies:
/// a comment inside a string is text, a quote inside an unquoted url( makes a bad URL, and an escape is read as its character.
/// </summary>
public static class CssTokenizer
{
    private const char Replacement = '�';
    private const int MaxEscapeHexDigits = 6;

    public enum TokenKind
    {
        /// <summary>The kind of <c>default(Token)</c>.</summary>
        None,
        Ident,
        Function,
        AtKeyword,
        Hash,
        String,
        BadString,
        Url,
        BadUrl,
        Delim,
        Number,
        Percentage,
        Dimension,
        Whitespace,
        Cdo,
        Cdc,
        Colon,
        Semicolon,
        Comma,
        OpenSquare,
        CloseSquare,
        OpenParen,
        CloseParen,
        OpenCurly,
        CloseCurly,
    }

    /// <param name="Value">The name of an ident, a function, an at-keyword or a hash, the text of a string or a URL, escapes read; the character of a delim.</param>
    public readonly record struct Token(TokenKind Kind, string Value);

    public static IList<Token> Tokenize(string css)
    {
        var reader = new Reader(Preprocess(css ?? string.Empty));
        var tokens = new List<Token>();
        while (reader.ReadToken() is { } token)
        {
            tokens.Add(token);
        }

        return tokens;
    }

    // CSS Syntax 3.3: line breaks are read as one, and NUL as the replacement character
    private static string Preprocess(string css) =>
        css.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\f', '\n').Replace('\0', Replacement);

    private sealed class Reader(string text)
    {
        private static readonly Dictionary<char, TokenKind> Punctuation = new()
        {
            ['('] = TokenKind.OpenParen,
            [')'] = TokenKind.CloseParen,
            ['['] = TokenKind.OpenSquare,
            [']'] = TokenKind.CloseSquare,
            ['{'] = TokenKind.OpenCurly,
            ['}'] = TokenKind.CloseCurly,
            [','] = TokenKind.Comma,
            [':'] = TokenKind.Colon,
            [';'] = TokenKind.Semicolon,
        };

        private int _position;

        public Token? ReadToken()
        {
            SkipComments();
            if (IsAtEnd(0))
            {
                return null;
            }

            var current = text[_position];
            if (IsWhitespace(current))
            {
                SkipWhitespace();
                return new Token(TokenKind.Whitespace, " ");
            }

            if (current is '"' or '\'')
            {
                _position++;
                return ReadString(current);
            }

            if (Punctuation.TryGetValue(current, out var kind))
            {
                _position++;
                return new Token(kind, current.ToString());
            }

            if (TryConsume("<!--"))
            {
                return new Token(TokenKind.Cdo, "<!--");
            }

            return TryConsume("-->") ? new Token(TokenKind.Cdc, "-->") : ReadNameNumberOrDelim();
        }

        private Token ReadNameNumberOrDelim()
        {
            if (StartsNumber(0))
            {
                return ReadNumeric();
            }

            if (StartsIdentSequence(0))
            {
                return ReadIdentLike();
            }

            var current = text[_position++];
            if (current == '#' && (IsIdentCodePoint(Peek(0)) || StartsEscape(0)))
            {
                return new Token(TokenKind.Hash, ReadIdentSequence());
            }

            return current == '@' && StartsIdentSequence(0) ? new Token(TokenKind.AtKeyword, ReadIdentSequence()) : Delim(current);
        }

        private static Token Delim(char value) => new(TokenKind.Delim, value.ToString());

        private char Peek(int offset) => _position + offset < text.Length ? text[_position + offset] : '\0';

        private bool IsAtEnd(int offset) => _position + offset >= text.Length;

        private bool TryConsume(string sequence)
        {
            if (!text.AsSpan(_position).StartsWith(sequence, StringComparison.Ordinal))
            {
                return false;
            }

            _position += sequence.Length;
            return true;
        }

        private void SkipComments()
        {
            while (TryConsume("/*"))
            {
                var end = text.IndexOf("*/", _position, StringComparison.Ordinal);
                _position = end < 0 ? text.Length : end;
                TryConsume("*/");
            }
        }

        // 4.3.5: a line break in a string makes it bad; an escaped one continues it
        private Token ReadString(char ending)
        {
            var value = new StringBuilder();
            while (true)
            {
                if (IsAtEnd(0))
                {
                    return new Token(TokenKind.String, value.ToString());
                }

                var current = text[_position];
                if (current == ending)
                {
                    _position++;
                    return new Token(TokenKind.String, value.ToString());
                }

                if (current == '\n')
                {
                    return new Token(TokenKind.BadString, value.ToString());
                }

                _position++;
                if (current != '\\')
                {
                    value.Append(current);
                }
                else if (Peek(0) == '\n')
                {
                    _position++;
                }
                else if (!IsAtEnd(0))
                {
                    value.Append(ReadEscape());
                }
            }
        }

        // 4.3.4: url( with a quoted URL is a function and the string a token of its own
        private Token ReadIdentLike()
        {
            var name = ReadIdentSequence();
            if (string.Equals(name, "url", StringComparison.OrdinalIgnoreCase) && Peek(0) == '(')
            {
                _position++;
                while (IsWhitespace(Peek(0)) && IsWhitespace(Peek(1)))
                {
                    _position++;
                }

                var next = IsWhitespace(Peek(0)) ? Peek(1) : Peek(0);

                return next is '"' or '\'' ? new Token(TokenKind.Function, name) : ReadUrl();
            }

            if (Peek(0) == '(')
            {
                _position++;
                return new Token(TokenKind.Function, name);
            }

            return new Token(TokenKind.Ident, name);
        }

        // 4.3.6: an unquoted url(
        private Token ReadUrl()
        {
            var value = new StringBuilder();
            SkipWhitespace();
            while (!IsAtEnd(0))
            {
                var current = text[_position++];
                if (current == ')')
                {
                    return new Token(TokenKind.Url, value.ToString());
                }

                if (IsWhitespace(current))
                {
                    return ReadUrlEnd(value);
                }

                if (!TryReadUrlCodePoint(current, value))
                {
                    return ReadBadUrl(value);
                }
            }

            return new Token(TokenKind.Url, value.ToString());
        }

        private Token ReadUrlEnd(StringBuilder value)
        {
            SkipWhitespace();
            if (IsAtEnd(0) || Peek(0) == ')')
            {
                _position = Math.Min(_position + 1, text.Length);
                return new Token(TokenKind.Url, value.ToString());
            }

            return ReadBadUrl(value);
        }

        private bool TryReadUrlCodePoint(char current, StringBuilder value)
        {
            if (current is '"' or '\'' or '(' || IsNonPrintable(current))
            {
                return false;
            }

            if (current != '\\')
            {
                value.Append(current);
                return true;
            }

            if (!IsValidEscape(current, Peek(0)))
            {
                return false;
            }

            value.Append(ReadEscape());
            return true;
        }

        // 4.3.14: the rest of a bad URL up to its ')', whose escapes do not close it
        private Token ReadBadUrl(StringBuilder value)
        {
            while (!IsAtEnd(0))
            {
                var current = text[_position++];
                if (current == ')')
                {
                    break;
                }

                if (current == '\\' && IsValidEscape(current, Peek(0)))
                {
                    ReadEscape();
                }
            }

            return new Token(TokenKind.BadUrl, value.ToString());
        }

        private Token ReadNumeric()
        {
            var number = ReadNumber();
            if (StartsIdentSequence(0))
            {
                return new Token(TokenKind.Dimension, number + ReadIdentSequence());
            }

            if (Peek(0) == '%')
            {
                _position++;
                return new Token(TokenKind.Percentage, number + "%");
            }

            return new Token(TokenKind.Number, number);
        }

        private string ReadNumber()
        {
            var start = _position;
            SkipSign();
            SkipDigits();
            if (Peek(0) == '.' && char.IsAsciiDigit(Peek(1)))
            {
                _position++;
                SkipDigits();
            }

            if (Peek(0) is 'e' or 'E' && StartsInteger(1))
            {
                _position++;
                SkipSign();
                SkipDigits();
            }

            return text[start.._position];
        }

        private string ReadIdentSequence()
        {
            var value = new StringBuilder();
            while (!IsAtEnd(0))
            {
                var current = Peek(0);
                if (IsIdentCodePoint(current))
                {
                    value.Append(current);
                    _position++;
                }
                else if (IsValidEscape(current, Peek(1)))
                {
                    _position++;
                    value.Append(ReadEscape());
                }
                else
                {
                    break;
                }
            }

            return value.ToString();
        }

        // 4.3.7: after the backslash; a code point that cannot be is the replacement character
        private string ReadEscape()
        {
            if (IsAtEnd(0))
            {
                return Replacement.ToString();
            }

            if (!char.IsAsciiHexDigit(Peek(0)))
            {
                return text[_position++].ToString();
            }

            var start = _position;
            while (_position - start < MaxEscapeHexDigits && char.IsAsciiHexDigit(Peek(0)))
            {
                _position++;
            }

            var codePoint = int.Parse(text.AsSpan(start, _position - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (IsWhitespace(Peek(0)))
            {
                _position++;
            }

            return codePoint == 0 || !Rune.IsValid(codePoint) ? Replacement.ToString() : char.ConvertFromUtf32(codePoint);
        }

        private bool StartsNumber(int offset) => Peek(offset) is '+' or '-' ? StartsUnsignedNumber(offset + 1) : StartsUnsignedNumber(offset);

        private bool StartsUnsignedNumber(int offset) => Peek(offset) == '.' ? char.IsAsciiDigit(Peek(offset + 1)) : char.IsAsciiDigit(Peek(offset));

        private bool StartsInteger(int offset) => Peek(offset) is '+' or '-' ? char.IsAsciiDigit(Peek(offset + 1)) : char.IsAsciiDigit(Peek(offset));

        private bool StartsIdentSequence(int offset) => Peek(offset) switch
        {
            '-' => IsIdentStart(Peek(offset + 1)) || Peek(offset + 1) == '-' || StartsEscape(offset + 1),
            '\\' => StartsEscape(offset),
            var first => IsIdentStart(first),
        };

        private bool StartsEscape(int offset) => IsValidEscape(Peek(offset), Peek(offset + 1));

        private void SkipSign()
        {
            if (Peek(0) is '+' or '-')
            {
                _position++;
            }
        }

        private void SkipWhitespace()
        {
            while (IsWhitespace(Peek(0)))
            {
                _position++;
            }
        }

        private void SkipDigits()
        {
            while (char.IsAsciiDigit(Peek(0)))
            {
                _position++;
            }
        }

        private static bool IsWhitespace(char value) => value is '\n' or '\t' or ' ';

        private static bool IsIdentStart(char value) => char.IsAsciiLetter(value) || value == '_' || value >= '\u0080';

        private static bool IsIdentCodePoint(char value) => IsIdentStart(value) || char.IsAsciiDigit(value) || value == '-';

        private static bool IsValidEscape(char first, char second) => first == '\\' && second != '\n' && second != '\0';

        private static bool IsNonPrintable(char value) =>
            value is '\u000B' or '\u007F' || char.IsBetween(value, '\u0000', '\u0008') || char.IsBetween(value, '\u000E', '\u001F');
    }
}
