using System.Text;
using Sodium;

namespace Bloom.Identity.Crypto;

/// <summary>Argon2id (libsodium <c>crypto_pwhash_str</c>, p = 1) with t = 3, m = 64 MiB (06-identity §6.1, ID-07).</summary>
public sealed class PasswordHasher
{
    private const long OpsLimit = 3;
    private const int MemLimit = 67_108_864;

    private readonly string _dummy;
    private long _verifications;

    public PasswordHasher() => _dummy = Hash(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));

    /// <summary>Count of Argon2id verifications, so ID-T03 can assert "exactly one".</summary>
    internal long Verifications => Interlocked.Read(ref _verifications);

    public string Hash(string normalizedPassword) => PasswordHash.ArgonHashString(normalizedPassword, OpsLimit, MemLimit);

    /// <summary>A null hash (unknown or unusable account) verifies against the dummy hash, so the cost is the same, and never succeeds.</summary>
    public (bool Ok, bool NeedsRehash) Verify(string? hash, string normalizedPassword)
    {
        if (hash is null)
        {
            VerifyDummy(normalizedPassword);
            return (false, false);
        }

        Interlocked.Increment(ref _verifications);
        if (!PasswordHash.ArgonHashStringVerify(hash, normalizedPassword)) return (false, false);
        return (true, PasswordHash.ArgonPasswordNeedsRehash(Encoding.UTF8.GetBytes(hash), OpsLimit, MemLimit));
    }

    public void VerifyDummy(string normalizedPassword)
    {
        Interlocked.Increment(ref _verifications);
        PasswordHash.ArgonHashStringVerify(_dummy, normalizedPassword);
    }
}
