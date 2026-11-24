namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>How the custom section checks write a value or a list into a message.</summary>
public static class CustomSectionText
{
    private const int MaxQuoteLength = 60;

    public static char[] Whitespace { get; } = [' ', '\t', '\n', '\r', '\f'];

    // besides the whitespace, what a reader takes for the end of a line
    private static readonly char[] LineBreaks = [.. Whitespace, '\v', '\u0085', '\u2028', '\u2029'];

    /// <summary>
    /// The value on one line, cut short, with each single quote doubled as in a single-quoted YAML text, so that between single quotes it
    /// cannot end the quote. A backslash stays as it is: a message about a link may be about one.
    /// </summary>
    public static string Quote(string value)
    {
        var line = string.Join(' ', (value ?? string.Empty).Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries));
        var shown = line.Length > MaxQuoteLength ? line[..MaxQuoteLength] + "…" : line;

        return shown.Replace("'", "''", StringComparison.Ordinal);
    }

    public static string JoinOr(IEnumerable<string> items) => Join(items, "or");

    public static string JoinAnd(IEnumerable<string> items) => Join(items, "and");

    private static string Join(IEnumerable<string> items, string last)
    {
        var list = items.ToList();

        return list.Count < 2 ? string.Concat(list) : $"{string.Join(", ", list[..^1])} {last} {list[^1]}";
    }
}
