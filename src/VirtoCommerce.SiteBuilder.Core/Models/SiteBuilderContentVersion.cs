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
        return CreateFromContentHash(group, Convert.ToHexString(SHA256.HashData(content)));
    }

    private static string CreateFromContentHash(GroupedSiteBuilderPage group, string contentHash)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            group.Id,
            group.StoreId,
            CreatedDateTicks = group.CreatedDate.Ticks,
            ContentHash = contentHash,
        });
        return $"\"{Convert.ToHexString(SHA256.HashData(bytes))}\"";
    }
}
