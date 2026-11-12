using System.Text;
using Bloom.Identity.Errors;
using Bloom.Identity.Validation;

namespace Bloom.Identity.Crypto;

/// <summary>NFKC, 12-128 Unicode scalars, not in the bundled breached list (spec §7; RF5).</summary>
public sealed class PasswordPolicy
{
    private static readonly Lazy<HashSet<string>> Breached = new(() =>
    {
        using var stream = typeof(PasswordPolicy).Assembly.GetManifestResourceStream("breached-passwords.txt")
            ?? throw new InvalidOperationException("breached-passwords.txt is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line) set.Add(line.Trim());
        return set;
    });

    public string Normalize(string pw) => pw.Normalize(NormalizationForm.FormKC);

    /// <summary>Returns the NFKC form, or throws <c>PASSWORD_WEAK</c>.</summary>
    public string Validate(string pw)
    {
        var normalized = Normalize(pw);
        var length = Inputs.Scalars(normalized);
        if (length is < 12 or > 128)
            throw IdentityErrors.Error(IdentityErrors.PasswordWeak, "The password must be 12 to 128 characters.");
        if (Breached.Value.Contains(normalized))
            throw IdentityErrors.Error(IdentityErrors.PasswordWeak, "This password is too common.");
        return normalized;
    }
}
