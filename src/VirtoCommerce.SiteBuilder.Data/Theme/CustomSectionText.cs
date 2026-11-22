namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <summary>How the custom section checks write a value or a list into a message.</summary>
public static class CustomSectionText
{
    private const int MaxQuoteLength = 60;

    public static char[] Whitespace { get; } = [' ', '\t', '\n', '\r', '\f'];

    public static string Quote(string value)
    {
        var line = string.Join(' ', value.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries));

        return line.Length > MaxQuoteLength ? line[..MaxQuoteLength] + "…" : line;
    }

    public static string JoinOr(IEnumerable<string> items) => Join(items, "or");

    public static string JoinAnd(IEnumerable<string> items) => Join(items, "and");

    private static string Join(IEnumerable<string> items, string last)
    {
        var list = items.ToList();

        return list.Count < 2 ? string.Concat(list) : $"{string.Join(", ", list[..^1])} {last} {list[^1]}";
    }
}
