using System.Security.Cryptography;
using System.Text;

namespace Bloom.Payment.Stripe;

/// <summary>Stripe's <c>Stripe-Signature</c> check (spec §7): <c>t=</c>, one or more <c>v1=</c>, HMAC-SHA256 of <c>"{t}.{body}"</c>, 5 minutes of tolerance.</summary>
public static class WebhookSignature
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>True when one <c>v1</c> matches and <c>t</c> is within the tolerance of <paramref name="now"/>; any parse failure is false.</summary>
    public static bool Verify(string? header, ReadOnlySpan<byte> body, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(header)) return false;
        long? t = null;
        var macs = new List<byte[]>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var key = kv[0].Trim();
            if (key == "t" && long.TryParse(kv[1], out var seconds)) t = seconds;
            else if (key == "v1" && kv[1].Length % 2 == 0 && kv[1].All(char.IsAsciiHexDigit)) macs.Add(Convert.FromHexString(kv[1])); // a malformed v1 is skipped
        }
        if (t is null || macs.Count == 0) return false;
        if (t < DateTimeOffset.MinValue.ToUnixTimeSeconds() || t > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return false;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(t.Value)).Duration() > Tolerance) return false;

        var payload = new byte[Encoding.UTF8.GetByteCount($"{t}.") + body.Length];
        var prefix = Encoding.UTF8.GetBytes($"{t}.", payload);
        body.CopyTo(payload.AsSpan(prefix));
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
        return macs.Any(mac => CryptographicOperations.FixedTimeEquals(mac, expected));
    }
}
