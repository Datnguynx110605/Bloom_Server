namespace Bloom.Inventory.Domain;

/// <summary>Serial and IMEI formats (02-inventory §9, plan R14).</summary>
public static class Identifiers
{
    /// <summary>Trimmed and upper-cased serial, or null when it isn't 1–32 characters of <c>A-Z0-9</c>.</summary>
    public static string? NormalizeSerial(string serial)
    {
        var s = serial.Trim().ToUpperInvariant();
        return s.Length is >= 1 and <= 32 && s.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) ? s : null;
    }

    /// <summary>15 digits with a valid Luhn check digit.</summary>
    public static bool IsValidImei(string imei)
    {
        if (imei.Length != 15 || !imei.All(char.IsAsciiDigit)) return false;
        var sum = imei.Reverse().Select((c, i) => (c - '0') * (i % 2 == 1 ? 2 : 1)).Sum(d => d / 10 + d % 10);
        return sum % 10 == 0;
    }
}
