using System.Security.Cryptography;
using System.Text;

namespace Soap.Services;

/// <summary>
/// 16-char hex SHA-256 hash of a URL. Used as the cache filename and the public
/// /v/{id} URL slug. Deterministic — same URL always yields the same id.
/// </summary>
public static class UrlHash
{
    public static string Compute(string url)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
