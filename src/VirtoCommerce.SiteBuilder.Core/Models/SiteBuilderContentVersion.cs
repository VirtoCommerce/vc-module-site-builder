using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public static class SiteBuilderContentVersion
{
    private const int HashLength = 64;
    private const int SurroundingQuoteCount = 2;
    private const int FileContentVersionLength = 32;
    private static readonly SearchValues<char> HexCharacters = SearchValues.Create("0123456789ABCDEF");

    public static bool IsWellFormed(string eTag)
    {
        return eTag is { Length: HashLength + SurroundingQuoteCount } && eTag[0] == '"' && eTag[^1] == '"'
            && !eTag.AsSpan(1, HashLength).ContainsAnyExcept(HexCharacters);
    }

    // Bind the content to the authorized aggregate, including its lifetime and store.
    // NULL (never seeded) and an intentionally empty document must have distinct versions.
    public static string Create(GroupedSiteBuilderPage group, string content)
    {
        return content == null ? CreateFromContentHash(group, null) : CreateFromUtf8(group, Encoding.UTF8.GetBytes(content));
    }

    public static string CreateFromUtf8(GroupedSiteBuilderPage group, ReadOnlySpan<byte> content)
    {
        return CreateFromContentHash(group, HashContent(content));
    }

    public static string CreateForThemeFile(string storeId, string path, string content)
    {
        return Hash(new
        {
            StoreId = storeId,
            ThemeFile = path,
            ContentHash = content == null ? null : HashContent(Encoding.UTF8.GetBytes(content)),
        });
    }

    /// <summary>The version of a draft of a page or a blog article kept as a file, as the discard of the draft compares it.</summary>
    public static string CreateForFilePage(string storeId, string contentType, string path, string content)
    {
        return Hash(new
        {
            StoreId = storeId,
            ContentType = contentType,
            FilePage = path,
            ContentHash = content == null ? null : HashContent(Encoding.UTF8.GetBytes(content)),
        });
    }

    /// <summary>
    /// The version of the content of a page or a blog article kept as a file, as it is read: the first 32 lowercase hex digits of the SHA-256 of
    /// its UTF-8 bytes; <c>null</c> for no content. A caller that read the content sends it back to write only over what it read.
    /// </summary>
    public static string CreateForFileContent(string content)
    {
        return content == null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..FileContentVersionLength];
    }

    /// <summary>The version of the settings of a page group, as an update of them compares it: a change of any of them, by anyone, moves it.</summary>
    public static string CreateForGroupSettings(GroupedSiteBuilderPage group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return Hash(new
        {
            group.Id,
            CreatedDateTicks = group.CreatedDate.Ticks,
            group.StoreId,
            group.Name,
            group.Permalink,
            group.CultureName,
            group.OrganizationId,
            group.Visibility,
            group.UserGroups,
            StartDateTicks = group.StartDate?.Ticks,
            EndDateTicks = group.EndDate?.Ticks,
            group.MultilanguagePageId,
            group.OverriddenFields,
            SourceRevisionDateTicks = group.SourceRevisionDate?.Ticks,
        });
    }

    /// <summary>The version of a multilanguage page, as an update of it compares it: a change of any of its values, by anyone, moves it.</summary>
    public static string CreateForMultilanguagePage(MultilanguagePage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return Hash(new
        {
            page.Id,
            CreatedDateTicks = page.CreatedDate.Ticks,
            page.StoreId,
            page.Name,
            page.Permalink,
            page.DefaultCultureName,
            page.OrganizationId,
            page.Visibility,
            page.UserGroups,
            StartDateTicks = page.StartDate?.Ticks,
            EndDateTicks = page.EndDate?.Ticks,
        });
    }

    private static string CreateFromContentHash(GroupedSiteBuilderPage group, string contentHash)
    {
        return Hash(new
        {
            group.Id,
            group.StoreId,
            CreatedDateTicks = group.CreatedDate.Ticks,
            ContentHash = contentHash,
        });
    }

    private static string HashContent(ReadOnlySpan<byte> content) => Convert.ToHexString(SHA256.HashData(content));

    private static string Hash<T>(T identity) => $"\"{HashContent(JsonSerializer.SerializeToUtf8Bytes(identity))}\"";
}
