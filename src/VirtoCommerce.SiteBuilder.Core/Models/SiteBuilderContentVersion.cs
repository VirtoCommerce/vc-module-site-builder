using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VirtoCommerce.SiteBuilder.Core.Models;

public static class SiteBuilderContentVersion
{
    private const int HashLength = 64;
    private const int SurroundingQuoteCount = 2;
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
