using Bloom.Identity.Data;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bloom.Identity.Sessions;

/// <summary>Signs access tokens (06-identity §4.3, plan I9).</summary>
public sealed class TokenIssuer(SigningKeys keys)
{
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(15);

    // iat is the issuing operation's database now (the gateway compares it with tokens_valid_after), so no default times (and no nbf).
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <param name="now">The database's now() read by the issuing operation: <c>iat</c>, and <c>exp</c> is 15 minutes later.</param>
    public string CreateAccessToken(Account account, Guid familyId, DateTimeOffset authTime, DateTimeOffset now)
    {
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
