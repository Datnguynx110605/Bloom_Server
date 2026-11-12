using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Bloom.Identity.Crypto;

/// <summary>256-bit opaque tokens (refresh, invitation, MFA challenge); only the SHA-256 is stored.</summary>
public static class Opaque
{
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] Sha256(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
