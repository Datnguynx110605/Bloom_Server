using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bloom.Gateway;

/// <summary>Validates access tokens (LLD 07 §6.1, plan P6).</summary>
public sealed class TokenValidator(JwksCache jwks, TimeProvider time)
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    private readonly JsonWebTokenHandler _handler = new();

    /// <returns>The caller, or null when the token must be answered with <c>TOKEN_INVALID</c>.</returns>
    public async Task<Caller?> ValidateAsync(string token)
    {
        JsonWebToken jwt;
        try { jwt = _handler.ReadJsonWebToken(token); }
        catch (ArgumentException) { return null; } // not a JWT
        // Checked before any refetch, so a forged header cannot make the gateway fetch the JWKS.
        if (jwt.Alg != SecurityAlgorithms.EcdsaSha256 || string.IsNullOrEmpty(jwt.Kid))
            return null;
        if (!jwks.Keys.Any(k => k.KeyId == jwt.Kid) && !await jwks.RefreshForUnknownKidAsync(jwt.Kid))
            return null;

        var result = await _handler.ValidateTokenAsync(jwt, new TokenValidationParameters
        {
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidIssuer = "bloom-identity",
            ValidAudience = "bloom",
            IssuerSigningKeys = jwks.Keys,
            RequireSignedTokens = true,
            ClockSkew = ClockSkew,
            // exp and nbf are judged on the injected clock, the same one the JWKS timing uses.
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return expires is { } exp && now <= exp + ClockSkew && (notBefore is not { } nbf || now >= nbf - ClockSkew);
            },
        });
        if (!result.IsValid)
            return null;

        if (!Guid.TryParseExact(jwt.Subject, "D", out var userId)
            || !jwt.TryGetPayloadValue<string>("typ", out var type) || type is not ("customer" or "staff")
            || !jwt.TryGetPayloadValue<long>("iat", out var issuedAt)
            || !jwt.TryGetPayloadValue<long>("auth_time", out var authTime)
            || time.GetUtcNow().ToUnixTimeSeconds() + ClockSkew.TotalSeconds < issuedAt)
            return null;
        string? role = null;
        if (type == "staff" && (!jwt.TryGetPayloadValue("role", out role) || string.IsNullOrEmpty(role)))
            return null;
        return new Caller(userId, type, role, authTime, issuedAt);
    }
}
