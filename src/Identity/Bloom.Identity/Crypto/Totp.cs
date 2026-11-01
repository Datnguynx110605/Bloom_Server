using System.Security.Cryptography;

namespace Bloom.Identity.Crypto;

/// <summary>RFC 6238: HMAC-SHA1, 6 digits, 30 s steps (06-identity §6.1).</summary>
public static class Totp
{
    private const int StepSeconds = 30;

    public static string Compute(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> mac = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, mac);
        var offset = mac[^1] & 0x0F;
        var binary = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(mac[offset..]) & 0x7FFFFFFF;
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>Accepts the current step and one either side; <paramref name="step"/> is the step that matched.</summary>
    public static bool Verify(byte[] secret, string code, DateTimeOffset now, out long step)
    {
        var current = now.ToUnixTimeSeconds() / StepSeconds;
        var given = System.Text.Encoding.UTF8.GetBytes(code);
        step = 0;
        var found = false;
        for (var s = current - 1; s <= current + 1; s++)
        {
            if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(Compute(secret, s)), given)) continue;
            step = s;
            found = true;
        }
        return found;
    }
}
