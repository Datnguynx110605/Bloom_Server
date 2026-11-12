using System.Net.Mail;
using System.Text.RegularExpressions;
using Bloom.Identity.Errors;

namespace Bloom.Identity.Validation;

/// <summary>Input checks of plan I23 and I24; each throws <c>VALIDATION_FAILED</c> naming the field.</summary>
public static partial class Inputs
{
    private static readonly HashSet<string> States = new(StringComparer.Ordinal)
    {
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA", "ME", "MD",
        "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC", "ND", "OH", "OK", "OR", "PA", "RI", "SC",
        "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY", "DC",
    };

    [GeneratedRegex(@"^[0-9]{5}(-[0-9]{4})?\z")] private static partial Regex ZipPattern();
    [GeneratedRegex(@"^\+[1-9][0-9]{1,14}\z")] private static partial Regex PhonePattern();

    /// <summary>Length in Unicode scalar values, not UTF-16 units.</summary>
    internal static int Scalars(string s) => s.EnumerateRunes().Count();

    public static (string Normalized, string Display) Email(string raw, string field = "email")
    {
        var display = raw.Trim();
        if (display.Length is 0 or > 254
            || !MailAddress.TryCreate(display, out var mail)
            || mail.Address != display
            || mail.User.Length is 0 or > 64)
            throw IdentityErrors.Validation(field, "Enter a valid email address.");
        return (display.ToLowerInvariant(), display);
    }

    public static string Name(string raw) => Text(raw, "name", 1);

    /// <summary>Trimmed text of <paramref name="min"/>-100 scalars (address <c>recipient</c>, <c>line1</c>, <c>city</c>, <c>label</c>, <c>line2</c>).</summary>
    public static string Text(string raw, string field, int min)
    {
        var text = raw.Trim();
        if (Scalars(text) < min || Scalars(text) > 100)
            throw IdentityErrors.Validation(field, $"{field} must be {min} to 100 characters.");
        return text;
    }

    public static string State(string raw) =>
        States.Contains(raw) ? raw : throw IdentityErrors.Validation("state", "Enter a US state code.");

    public static string Zip(string raw) =>
        ZipPattern().IsMatch(raw) ? raw : throw IdentityErrors.Validation("zip", "Enter a 5-digit or ZIP+4 code.");

    /// <summary>Optional: null or empty means no phone.</summary>
    public static string? Phone(string? raw) =>
        string.IsNullOrEmpty(raw) ? null
        : PhonePattern().IsMatch(raw) ? raw
        : throw IdentityErrors.Validation("phone", "Enter the phone number in E.164 format.");
}
