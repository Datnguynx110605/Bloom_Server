using System.Security.Cryptography;
using System.Text.Json;
using Bloom.Identity.Errors;
using Microsoft.AspNetCore.DataProtection;

namespace Bloom.Identity.Codes;

/// <summary>Registration token (06-identity §4.3, plan I4): Data Protection, purpose <c>bloom.registration.v1</c>, 30 min, payload <c>{email, verifiedAt}</c>.</summary>
public sealed class RegistrationTokens(IDataProtectionProvider provider, TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly ITimeLimitedDataProtector _protector = provider.CreateProtector("bloom.registration.v1").ToTimeLimitedDataProtector();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Payload(string Email, DateTimeOffset VerifiedAt);

    /// <param name="displayEmail">The trimmed address the visitor typed (I33).</param>
    public string Create(string displayEmail) =>
        _protector.Protect(JsonSerializer.Serialize(new Payload(displayEmail, time.GetUtcNow()), Json), Lifetime);

    /// <summary>The display email in the token.</summary>
    /// <exception cref="Grpc.Core.RpcException"><c>REGISTRATION_TOKEN_INVALID</c> when the token is tampered, expired or older than 30 min on the app's clock.</exception>
    public string Read(string token)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(_protector.Unprotect(token), Json);
            if (payload is { Email.Length: > 0 } && time.GetUtcNow() - payload.VerifiedAt <= Lifetime) return payload.Email;
        }
        catch (Exception e) when (e is CryptographicException or JsonException) { }
        throw IdentityErrors.Error(IdentityErrors.RegistrationTokenInvalid, "Verify your email again.");
    }
}
