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
    private const int MaxCodePoint = 0x10FFFF;

    public enum TokenKind
    {
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
        private int _position;

        public Token? ReadToken()
        {
            SkipComments();
            if (_position >= text.Length)
            {
                return null;
            }

            var current = text[_position++];
            switch (current)
            {
                case '\n' or '\t' or ' ':
                    while (IsWhitespace(Peek(0)))
                    {
                        _position++;
                    }

                    return new Token(TokenKind.Whitespace, " ");
                case '"' or '\'':
                    return ReadString(current);
                case '#':
                    if (IsIdentCodePoint(Peek(0)) || IsValidEscape(Peek(0), Peek(1)))
                    {
                        return new Token(TokenKind.Hash, ReadIdentSequence());
                    }

                    return Delim(current);
                case '(':
                    return new Token(TokenKind.OpenParen, "(");
                case ')':
                    return new Token(TokenKind.CloseParen, ")");
                case '[':
                    return new Token(TokenKind.OpenSquare, "[");
                case ']':
                    return new Token(TokenKind.CloseSquare, "]");
                case '{':
                    return new Token(TokenKind.OpenCurly, "{");
                case '}':
                    return new Token(TokenKind.CloseCurly, "}");
                case ',':
                    return new Token(TokenKind.Comma, ",");
                case ':':
                    return new Token(TokenKind.Colon, ":");
                case ';':
                    return new Token(TokenKind.Semicolon, ";");
                case '+' or '.':
                    _position--;
                    return StartsNumber(0) ? ReadNumeric() : Delim(text[_position++]);
                case '-':
                    _position--;
                    if (StartsNumber(0))
                    {
                        return ReadNumeric();
                    }

                    if (Peek(1) == '-' && Peek(2) == '>')
                    {
                        _position += 3;
                        return new Token(TokenKind.Cdc, "-->");
                    }

                    if (StartsIdentSequence(0))
                    {
                        return ReadIdentLike();
                    }

                    return Delim(text[_position++]);
                case '<':
                    if (Peek(0) == '!' && Peek(1) == '-' && Peek(2) == '-')
                    {
                        _position += 3;
                        return new Token(TokenKind.Cdo, "<!--");
                    }

                    return Delim(current);
                case '@':
                    return StartsIdentSequence(0) ? new Token(TokenKind.AtKeyword, ReadIdentSequence()) : Delim(current);
                case '\\':
                    _position--;
                    return IsValidEscape(Peek(0), Peek(1)) ? ReadIdentLike() : Delim(text[_position++]);
                default:
                    _position--;
                    if (char.IsAsciiDigit(current))
                    {
                        return ReadNumeric();
                    }

                    return IsIdentStart(current) ? ReadIdentLike() : Delim(text[_position++]);
            }
        }

        private static Token Delim(char value) => new(TokenKind.Delim, value.ToString());

        private char Peek(int offset) => _position + offset < text.Length ? text[_position + offset] : '\0';

        private bool IsAtEnd(int offset) => _position + offset >= text.Length;

        private void SkipComments()
        {
            while (Peek(0) == '/' && Peek(1) == '*')
            {
                var end = text.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                _position = end < 0 ? text.Length : end + 2;
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

        // 4.3.6
        private Token ReadUrl()
        {
            var value = new StringBuilder();
            SkipWhitespace();
            while (true)
            {
                if (IsAtEnd(0))
                {
                    return new Token(TokenKind.Url, value.ToString());
                }

                var current = text[_position++];
                if (current == ')')
                {
                    return new Token(TokenKind.Url, value.ToString());
                }

                if (IsWhitespace(current))
                {
                    SkipWhitespace();
                    if (IsAtEnd(0) || Peek(0) == ')')
                    {
                        _position = Math.Min(_position + 1, text.Length);
                        return new Token(TokenKind.Url, value.ToString());
                    }

                    return ReadBadUrl(value);
                }

                if (current is '"' or '\'' or '(' || IsNonPrintable(current))
                {
                    return ReadBadUrl(value);
                }

                if (current == '\\')
                {
                    if (!IsValidEscape(current, Peek(0)))
                    {
                        return ReadBadUrl(value);
                    }

                    value.Append(ReadEscape());
                }
                else
                {
                    value.Append(current);
                }
            }
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
            var start = _position;
            if (Peek(0) is '+' or '-')
            {
                _position++;
            }

            SkipDigits();
            if (Peek(0) == '.' && char.IsAsciiDigit(Peek(1)))
            {
                _position++;
                SkipDigits();
            }

            if (Peek(0) is 'e' or 'E' && (char.IsAsciiDigit(Peek(1)) || (Peek(1) is '+' or '-' && char.IsAsciiDigit(Peek(2)))))
            {
                _position += 2;
                SkipDigits();
            }

            var number = text[start.._position];
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
            while (_position - start < 6 && char.IsAsciiHexDigit(Peek(0)))
            {
                _position++;
            }

            var codePoint = int.Parse(text.AsSpan(start, _position - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (IsWhitespace(Peek(0)))
            {
                _position++;
            }

            return codePoint is 0 or > MaxCodePoint or (>= 0xD800 and <= 0xDFFF) ? Replacement.ToString() : char.ConvertFromUtf32(codePoint);
        }

        private bool StartsNumber(int offset) => Peek(offset) switch
        {
            '+' or '-' => char.IsAsciiDigit(Peek(offset + 1)) || (Peek(offset + 1) == '.' && char.IsAsciiDigit(Peek(offset + 2))),
            '.' => char.IsAsciiDigit(Peek(offset + 1)),
            var digit => char.IsAsciiDigit(digit),
        };

        private bool StartsIdentSequence(int offset) => Peek(offset) switch
        {
            '-' => IsIdentStart(Peek(offset + 1)) || Peek(offset + 1) == '-' || IsValidEscape(Peek(offset + 1), Peek(offset + 2)),
            '\\' => IsValidEscape(Peek(offset), Peek(offset + 1)),
            var first => IsIdentStart(first),
        };

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

        private static bool IsNonPrintable(char value) => value is <= '\u0008' or '\u000B' or (>= '\u000E' and <= '\u001F') or '\u007F';
    }
}
