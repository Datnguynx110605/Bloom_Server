using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Bloom.Identity.Tests;

/// <summary>Google's JWKS endpoint and ID-token minting, on Kestrel <c>127.0.0.1:0</c> (plan I1). Tokens are RS256.</summary>
public sealed class FakeGoogle : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<(string Kid, RSA Key)> _keys = [];
    private int _requests;

    private FakeGoogle(WebApplication app)
    {
        _app = app;
        RotateKey();
    }

    public string ClientId => "fake-google-client-id";

    public Uri JwksUrl { get; private set; } = null!;

    /// <summary>How many times the JWKS has been fetched.</summary>
    public int JwksRequests => Volatile.Read(ref _requests);

    public static async Task<FakeGoogle> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var google = new FakeGoogle(app);
        app.MapGet("/certs", () =>
        {
            Interlocked.Increment(ref google._requests);
            return Results.Text(google.JwksJson(), "application/json");
        });
        await app.StartAsync();
        google.JwksUrl = new Uri(new Uri(app.Urls.First()), "/certs");
        return google;
    }

    /// <summary>Adds a new key and signs later tokens with it; the old keys stay in the JWKS.</summary>
    public void RotateKey()
    {
        lock (_keys) _keys.Add(($"google-key-{_keys.Count + 1}", RSA.Create(2048)));
    }

    public string Mint(
        string sub, string email, bool emailVerified = true, string? name = "Ann Lee", string? aud = null, string? iss = null, DateTimeOffset? exp = null, string? kid = null)
    {
        (string Kid, RSA Key) signer;
        lock (_keys) signer = _keys[^1];
        var now = DateTimeOffset.UtcNow;
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = iss ?? "https://accounts.google.com",
            ["aud"] = aud ?? ClientId,
            ["sub"] = sub,
            ["email"] = email,
            ["email_verified"] = emailVerified,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = (exp ?? now.AddHours(1)).ToUnixTimeSeconds(),
        };
        if (name is not null) payload["name"] = name;
        var signingInput = Encode(new { alg = "RS256", kid = kid ?? signer.Kid, typ = "JWT" }) + "." + Encode(payload);
        var signature = signer.Key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64UrlEncoder.Encode(signature);
    }

    private static string Encode(object value) => Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(value));

    private string JwksJson()
    {
        lock (_keys)
            return JsonSerializer.Serialize(new
            {
                keys = _keys.Select(k =>
                {
                    var p = k.Key.ExportParameters(false);
                    return new { kty = "RSA", use = "sig", alg = "RS256", kid = k.Kid, n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) };
                }),
            });
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        lock (_keys) foreach (var (_, key) in _keys) key.Dispose();
    }
}
