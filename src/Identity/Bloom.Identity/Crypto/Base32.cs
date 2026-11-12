namespace Bloom.Identity.Crypto;

/// <summary>RFC 4648 base32, case-insensitive, optional trailing <c>=</c> padding.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static bool TryDecode(string s, out byte[] bytes)
    {
        bytes = [];
        var text = s.TrimEnd('=');
        if (text.Length == 0 || s.Length - text.Length > 6 || text.Length % 8 is 1 or 3 or 6) return false;

        var result = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            var value = Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (value < 0) return false;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            result.Add((byte)(buffer >> bits));
            buffer &= (1 << bits) - 1;
        }

        bytes = [.. result];
        return true;
    }
}
