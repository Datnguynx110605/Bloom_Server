using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bloom.LoadTest.Fakes;

/// <summary>Google's JWKS and ID-token minting (ported from Bloom.Identity.Tests' FakeGoogle): one RS256 key, tokens for any email.</summary>
public sealed class GoogleFake(IConfiguration config)
{
    private const string Kid = "lt-google-1";

    private readonly RSA _key = RSA.Create(2048);
    private readonly string _clientId = config["Fakes:Google:ClientId"] ?? throw new InvalidOperationException("Fakes:Google:ClientId is not set.");

    public object Jwks()
    {
        var p = _key.ExportParameters(false);
        return new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = Kid, n = Base64Url.EncodeToString(p.Modulus), e = Base64Url.EncodeToString(p.Exponent) } } };
    }

    public string Mint(string email, string? name)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = "https://accounts.google.com",
            ["aud"] = _clientId,
            ["sub"] = "lt-" + email.ToLowerInvariant(),
            ["email"] = email,
            ["email_verified"] = true,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds(),
        };
        if (name is not null) payload["name"] = name;
        var input = Encode(new { alg = "RS256", kid = Kid, typ = "JWT" }) + "." + Encode(payload);
        return input + "." + Base64Url.EncodeToString(_key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static string Encode(object value) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));
}
