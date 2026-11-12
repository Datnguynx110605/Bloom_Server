using Bloom.Identity.Errors;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bloom.Identity.GoogleSignIn; // not ...Google: that would shadow the Google.Protobuf / Google.Rpc namespaces

public sealed record GoogleIdentity(string Sub, string Email, string? Name);

/// <summary>Verifies a Google ID token (06-identity §6.2, spec §7). Any failure, including an unreachable JWKS, is <c>INVALID_CREDENTIALS</c>.</summary>
public sealed class GoogleTokenVerifier(IHttpClientFactory httpFactory, IdentityOptions options, TimeProvider time, ILogger<GoogleTokenVerifier> log)
{
    public const string HttpClientName = "google-jwks";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1), UnknownKidInterval = TimeSpan.FromSeconds(30);
    private static readonly string[] Issuers = ["accounts.google.com", "https://accounts.google.com"];

    private readonly SemaphoreSlim _fetch = new(1, 1);
    private volatile Snapshot? _snapshot; // replaced, never mutated: a fresh hit reads it without the lock
    private DateTimeOffset _lastUnknownKidFetch = DateTimeOffset.MinValue; // under _fetch

    private sealed record Snapshot(JsonWebKeySet Keys, DateTimeOffset FetchedAt)
    {
        public bool Knows(string? kid) => Keys.Keys.Any(k => k.Kid == kid);
    }

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

    /// <summary>
    /// The cached JWKS (1 h). An unknown <paramref name="kid"/> refetches (Google rotates keys), at most once per 30 s across all kids (R42);
    /// the same rule as the gateway's JwksCache.
    /// </summary>
    private async Task<JsonWebKeySet> KeysAsync(string? kid)
    {
        if (_snapshot is { } hit && time.GetUtcNow() - hit.FetchedAt <= CacheLifetime && hit.Knows(kid)) return hit.Keys;

        await _fetch.WaitAsync();
        try
        {
            var now = time.GetUtcNow();
            var current = _snapshot;
            if (current is not null && now - current.FetchedAt <= CacheLifetime)
            {
                if (current.Knows(kid) || now - _lastUnknownKidFetch < UnknownKidInterval) return current.Keys; // fetched meanwhile, or cooling down
                _lastUnknownKidFetch = now;
            }
            using var client = httpFactory.CreateClient(HttpClientName);
            var keys = new JsonWebKeySet(await client.GetStringAsync(options.Google.JwksUrl));
            _snapshot = new Snapshot(keys, now);
            return keys;
        }
        finally
        {
            _fetch.Release();
        }
    }
}
