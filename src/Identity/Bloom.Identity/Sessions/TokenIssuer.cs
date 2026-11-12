using Bloom.Identity.Data;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bloom.Identity.Sessions;

/// <summary>Signs access tokens (06-identity §4.3, plan I9).</summary>
public sealed class TokenIssuer(SigningKeys keys, TimeProvider time)
{
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);

    // iat and exp come from the injected clock, so no default times (and no nbf).
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public string CreateAccessToken(Account account, Guid familyId, DateTimeOffset authTime)
    {
        var now = time.GetUtcNow();
        var claims = new Dictionary<string, object>
        {
            ["sub"] = account.Id.ToString(),
            ["typ"] = account.Type,
            ["auth_time"] = authTime.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString(),
            ["sid"] = familyId.ToString(),
        };
        if (account.Type == AccountTypes.Staff) claims["role"] = account.Role!;

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "bloom-identity",
            Audience = "bloom",
            Claims = claims,
            IssuedAt = now.UtcDateTime,
            Expires = (now + AccessLifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(keys.ActiveKey, SecurityAlgorithms.EcdsaSha256),
        });
    }
}
