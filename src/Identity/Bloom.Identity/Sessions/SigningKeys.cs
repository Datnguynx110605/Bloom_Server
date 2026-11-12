using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Bloom.Identity.Sessions;

/// <summary>The configured P-256 keys (06-identity §4.3): the active one signs, all are published in the JWKS.</summary>
public sealed class SigningKeys
{
    public SigningKeys(IdentityOptions options)
    {
        var jwks = new List<object>();
        ECDsaSecurityKey? active = null;
        foreach (var configured in options.SigningKeys)
        {
            var ecdsa = ECDsa.Create(); // lives as long as the singleton
            ecdsa.ImportFromPem(configured.PrivateKeyPem);
            var q = ecdsa.ExportParameters(false).Q;
            jwks.Add(new
            {
                kty = "EC", crv = "P-256", x = Base64UrlEncoder.Encode(q.X), y = Base64UrlEncoder.Encode(q.Y),
                kid = configured.Kid, use = "sig", alg = SecurityAlgorithms.EcdsaSha256,
            });
            if (configured.Kid == options.ActiveKeyId) active = new ECDsaSecurityKey(ecdsa) { KeyId = configured.Kid };
        }
        ActiveKey = active ?? throw new InvalidOperationException("Identity:ActiveKeyId is not one of Identity:SigningKeys."); // options.Validate() rules this out
        JwksJson = JsonSerializer.Serialize(new { keys = jwks });
    }

    public ECDsaSecurityKey ActiveKey { get; }

    public string JwksJson { get; }
}
