using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Bloom.Identity.Tests;

public sealed record TestSigningKey(string Kid, string Pem);

/// <summary>Random secrets for one test host: every key the API mode requires (plan I6) except the URLs, which appsettings.json holds.</summary>
public sealed class TestSecrets
{
    public required byte[] CodePepper { get; init; }
    public required byte[] CodeEncryptionKey { get; init; }
    public required byte[] TotpKey { get; init; }
    public required TestSigningKey SigningKey { get; init; }

    /// <summary>Self-signed RSA certificate, base64 PKCS#12 without password.</summary>
    public required string CertificateBase64 { get; init; }

    public static TestSecrets Create()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=bloom-identity-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return new()
        {
            CodePepper = RandomNumberGenerator.GetBytes(32),
            CodeEncryptionKey = RandomNumberGenerator.GetBytes(32),
            TotpKey = RandomNumberGenerator.GetBytes(32),
            SigningKey = new("test-key-1", ec.ExportPkcs8PrivateKeyPem()),
            CertificateBase64 = Convert.ToBase64String(cert.Export(X509ContentType.Pfx)),
        };
    }

    public IDictionary<string, string?> ToConfig() => new Dictionary<string, string?>
    {
        ["Identity:SigningKeys:0:Kid"] = SigningKey.Kid,
        ["Identity:SigningKeys:0:PrivateKeyPem"] = SigningKey.Pem,
        ["Identity:ActiveKeyId"] = SigningKey.Kid,
        ["Identity:CodePepper"] = Convert.ToBase64String(CodePepper),
        ["Identity:CodeEncryptionKey"] = Convert.ToBase64String(CodeEncryptionKey),
        ["Identity:TotpKey"] = Convert.ToBase64String(TotpKey),
        ["Identity:DataProtection:Certificate"] = CertificateBase64,
        ["Identity:Google:ClientId"] = "test-client-id",
    };
}
