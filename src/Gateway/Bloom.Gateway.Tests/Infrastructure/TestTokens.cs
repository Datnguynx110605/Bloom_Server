using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Bloom.Gateway.Tests;

/// <summary>A P-256 signing key as identity would publish it.</summary>
public sealed class TestKeys
{
    private TestKeys(string kid, ECDsa ecdsa, TimeProvider clock)
    {
        Kid = kid;
        Clock = clock;
        Ecdsa = ecdsa;
        var p = ecdsa.ExportParameters(false);
        PublicJwk = new JsonWebKey { Kty = "EC", Crv = "P-256", Kid = kid, Use = "sig", Alg = "ES256", X = Base64UrlEncoder.Encode(p.Q.X), Y = Base64UrlEncoder.Encode(p.Q.Y) };
    }

    public string Kid { get; }

    public ECDsa Ecdsa { get; }

    /// <summary>Time source for the default <c>iat</c> of tokens signed with this key: the gateway's clock for <c>TestGateway.DefaultKey</c>.</summary>
    public TimeProvider Clock { get; }

    public JsonWebKey PublicJwk { get; }

    public static TestKeys Create(string kid, TimeProvider? clock = null) => new(kid, ECDsa.Create(ECCurve.NamedCurves.nistP256), clock ?? TimeProvider.System);
}

/// <summary>Hand-built JWTs, so tests can break any claim or header.</summary>
public static class TestTokens
{
    /// <param name="edit">Edits the payload (add, replace or remove claims) before signing.</param>
    public static string Issue(TestKeys key, string typ = "customer", string? role = null, Guid? sub = null,
        DateTimeOffset? iat = null, TimeSpan? lifetime = null, Action<IDictionary<string, object>>? edit = null) =>
        Build("ES256", key.Kid, Payload(key.Clock, typ, role, sub, iat, lifetime, edit), data => key.Ecdsa.SignData(data, HashAlgorithmName.SHA256));

    public static string IssueHs256(TestKeys key) =>
        Build("HS256", key.Kid, Payload(key.Clock, "customer", null, null, null, null, null), data => HMACSHA256.HashData(new byte[32], data));

    public static string IssueRs256(TestKeys key)
    {
        using var rsa = RSA.Create(2048);
        return Build("RS256", key.Kid, Payload(key.Clock, "customer", null, null, null, null, null),
            data => rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    public static string IssueUnsigned(TestKeys key) => Build("none", key.Kid, Payload(key.Clock, "customer", null, null, null, null, null), null);

    private static Dictionary<string, object> Payload(TimeProvider clock, string typ, string? role, Guid? sub, DateTimeOffset? iat, TimeSpan? lifetime, Action<IDictionary<string, object>>? edit)
    {
        var issued = iat ?? clock.GetUtcNow();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = "bloom-identity",
            ["aud"] = "bloom",
            ["sub"] = (sub ?? Guid.NewGuid()).ToString("D"),
            ["typ"] = typ,
            ["auth_time"] = issued.ToUnixTimeSeconds(),
            ["iat"] = issued.ToUnixTimeSeconds(),
            ["exp"] = (issued + (lifetime ?? TimeSpan.FromMinutes(15))).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("D"),
        };
        if (role is not null)
            claims["role"] = role;
        edit?.Invoke(claims);
        return claims;
    }

    private static string Build(string alg, string kid, Dictionary<string, object> payload, Func<byte[], byte[]>? sign)
    {
        var signingInput = $"{Encode(new { alg, typ = "JWT", kid })}.{Encode(payload)}";
        return $"{signingInput}.{(sign is null ? "" : Base64UrlEncoder.Encode(sign(Encoding.ASCII.GetBytes(signingInput))))}";
    }

    private static string Encode(object json) => Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(json));
}
