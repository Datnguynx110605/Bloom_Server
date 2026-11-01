using System.Security.Cryptography;
using System.Text;

namespace Bloom.Identity.Crypto;

/// <summary>HMAC-SHA256(pepper, subject ‖ purpose ‖ code) (spec §7); subject = account id (<c>D</c> form), or the normalized email for <c>signup</c>.</summary>
public sealed class CodeHasher(byte[] pepper)
{
    public byte[] Hash(string subject, string purpose, string code) =>
        HMACSHA256.HashData(pepper, Encoding.UTF8.GetBytes(subject + purpose + code));
}
