using System.Security.Cryptography;
using Bloom.Identity.Crypto;

namespace Bloom.Identity.Tests;

public static class TestTotp
{
    /// <summary>A random secret as the client sends it (base32 text, 5 bits per character) and as the server decodes it.</summary>
    public static (string Text, byte[] Bytes) NewSecret(int chars = 32)
    {
        var text = RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567", chars); // 32 characters = 160 bits = the 20-byte minimum
        Assert.True(Base32.TryDecode(text, out var bytes));
        return (text, bytes);
    }

    /// <summary>The code of the current 30 s step plus <paramref name="stepOffset"/>.</summary>
    public static string CodeAt(TestIdentity t, byte[] secret, long stepOffset = 0) =>
        Totp.Compute(secret, t.Clock.GetUtcNow().ToUnixTimeSeconds() / 30 + stepOffset);
}
