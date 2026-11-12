using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Bloom.Identity;

public sealed class SigningKeyOptions
{
    public string Kid { get; set; } = "";
    public string PrivateKeyPem { get; set; } = "";
}

public sealed class DataProtectionOptions
{
    /// <summary>Base64 PKCS#12 (no password) that protects the Data Protection keys (plan I5).</summary>
    public string Certificate { get; set; } = "";
}

public sealed class GoogleOptions
{
    public string ClientId { get; set; } = "";
    public string JwksUrl { get; set; } = "";
}

/// <summary>Section <c>Identity</c> (plan I6). Worker mode needs only <see cref="Mode"/>, <see cref="Database"/> and <see cref="RabbitMq"/>.</summary>
public sealed class IdentityOptions
{
    public string Mode { get; set; } = "";
    public string Database { get; set; } = "";
    public string Redis { get; set; } = "";
    public string RabbitMq { get; set; } = "";
    public List<SigningKeyOptions> SigningKeys { get; set; } = [];
    public string ActiveKeyId { get; set; } = "";
    public string CodePepper { get; set; } = "";
    public string CodeEncryptionKey { get; set; } = "";
    public string TotpKey { get; set; } = "";
    public DataProtectionOptions DataProtection { get; set; } = new();
    public GoogleOptions Google { get; set; } = new();
    public string InviteBaseUrl { get; set; } = "";
    public string OrderUrl { get; set; } = "";

    public bool IsApi => Mode == "api";

    public byte[] CodePepperBytes { get; private set; } = [];
    public byte[] CodeEncryptionKeyBytes { get; private set; } = [];
    public byte[] TotpKeyBytes { get; private set; } = [];

    /// <summary>Throws <see cref="InvalidOperationException"/> naming the first missing or invalid key, and decodes the byte keys.</summary>
    public void Validate()
    {
        if (Mode is not ("api" or "worker"))
            throw Bad("Identity:Mode", $"must be 'api' or 'worker', not '{Mode}'");
        Require("Identity:Database", Database);
        Require("Identity:RabbitMq", RabbitMq);
        if (!IsApi) return;

        Require("Identity:Redis", Redis);
        Require("Identity:Google:ClientId", Google.ClientId);
        RequireUrl("Identity:Google:JwksUrl", Google.JwksUrl);
        RequireUrl("Identity:InviteBaseUrl", InviteBaseUrl);
        RequireUrl("Identity:OrderUrl", OrderUrl);

        if (SigningKeys.Count == 0) throw Bad("Identity:SigningKeys", "is required");
        for (var i = 0; i < SigningKeys.Count; i++)
        {
            Require($"Identity:SigningKeys:{i}:Kid", SigningKeys[i].Kid);
            try
            {
                using var key = ECDsa.Create();
                key.ImportFromPem(SigningKeys[i].PrivateKeyPem);
                if (key.KeySize != 256) throw new CryptographicException("not P-256");
            }
            catch (Exception e) when (e is CryptographicException or ArgumentException)
            {
                throw Bad($"Identity:SigningKeys:{i}:PrivateKeyPem", "must be a P-256 private key in PEM");
            }
        }
        if (!SigningKeys.Any(k => k.Kid == ActiveKeyId))
            throw Bad("Identity:ActiveKeyId", "must be the Kid of one of Identity:SigningKeys");

        CodePepperBytes = Key32("Identity:CodePepper", CodePepper);
        CodeEncryptionKeyBytes = Key32("Identity:CodeEncryptionKey", CodeEncryptionKey);
        TotpKeyBytes = Key32("Identity:TotpKey", TotpKey);

        Require("Identity:DataProtection:Certificate", DataProtection.Certificate);
        try
        {
            using var cert = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(DataProtection.Certificate), null);
            if (!cert.HasPrivateKey) throw new CryptographicException("no private key");
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            throw Bad("Identity:DataProtection:Certificate", "must be a base64 PKCS#12 certificate with a private key and no password");
        }
    }

    private static InvalidOperationException Bad(string key, string problem) => new($"{key} {problem}.");

    private static void Require(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Bad(key, "is required");
    }

    private static void RequireUrl(string key, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out _)) throw Bad(key, "must be an absolute URL");
    }

    private static byte[] Key32(string key, string value)
    {
        Require(key, value);
        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length == 32) return bytes;
        }
        catch (FormatException) { }
        throw Bad(key, "must be base64 of exactly 32 bytes");
    }
}
