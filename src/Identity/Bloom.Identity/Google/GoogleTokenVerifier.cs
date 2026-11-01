using Bloom.Identity.Errors;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bloom.Identity.GoogleSignIn; // not ...Google: that would shadow the Google.Protobuf / Google.Rpc namespaces

public sealed record GoogleIdentity(string Sub, string Email, string? Name);

/// <summary>Verifies a Google ID token (06-identity §6.2, spec §7). Any failure, including an unreachable JWKS, is <c>INVALID_CREDENTIALS</c>.</summary>
public sealed class GoogleTokenVerifier(IHttpClientFactory httpFactory, IdentityOptions options, TimeProvider time, ILogger<GoogleTokenVerifier> log)
{
    public const string HttpClientName = "google-jwks";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
    private static readonly string[] Issuers = ["accounts.google.com", "https://accounts.google.com"];

    private readonly SemaphoreSlim _fetch = new(1, 1);
    private JsonWebKeySet? _keys;
    private DateTimeOffset _fetchedAt;

    /// <exception cref="Grpc.Core.RpcException"><c>INVALID_CREDENTIALS</c></exception>
    public async Task<GoogleIdentity> VerifyAsync(string idToken)
    {
        try
        {
            var kid = new JsonWebToken(idToken).Kid; // throws on anything that is not a JWT
            var now = time.GetUtcNow();
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                IssuerSigningKeys = (await KeysAsync(kid)).GetSigningKeys(),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidIssuers = Issuers,
                ValidAudience = options.Google.ClientId,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                // The handler would read the system clock; expiry is judged on the app's TimeProvider.
                LifetimeValidator = (notBefore, expires, _, _) =>
                    expires is { } exp && exp > now.UtcDateTime && (notBefore is not { } nbf || nbf <= now.UtcDateTime),
                ClockSkew = TimeSpan.Zero,
            });
            if (result.IsValid
                && result.Claims.TryGetValue("email_verified", out var verified) && verified is true or "true"
                && result.Claims.TryGetValue("sub", out var sub) && sub is string { Length: > 0 } s
                && result.Claims.TryGetValue("email", out var email) && email is string { Length: > 0 } e)
                return new(s, e, result.Claims.TryGetValue("name", out var name) ? name as string : null);
        }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            log.LogWarning("Google token check failed: {Reason}", ex.GetType().Name); // never the token
        }
        throw IdentityErrors.Error(IdentityErrors.InvalidCredentials, "The Google sign-in is not valid.");
    }

    /// <summary>The cached JWKS (1 h), fetched again once when it lacks <paramref name="kid"/> (Google rotates keys).</summary>
    private async Task<JsonWebKeySet> KeysAsync(string? kid)
    {
        await _fetch.WaitAsync();
        try
        {
            var stale = _keys is null || time.GetUtcNow() - _fetchedAt > CacheLifetime;
            // ponytail: an unknown kid refetches every time, so a flood of forged kids hits Google; rely on the gateway's rate limit, add a cooldown if it matters.
            if (stale || _keys!.Keys.All(k => k.Kid != kid))
            {
                using var client = httpFactory.CreateClient(HttpClientName);
                _keys = new JsonWebKeySet(await client.GetStringAsync(options.Google.JwksUrl));
                _fetchedAt = time.GetUtcNow();
            }
            return _keys!;
        }
        finally
        {
            _fetch.Release();
        }
    }
}
