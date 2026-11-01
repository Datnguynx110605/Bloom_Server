namespace Bloom.Identity.Contracts;

/// <summary>Published to notification (email only). <c>EncryptedCode</c> = base64url(nonce ‖ ciphertext ‖ tag) of the UTF-8 code, AES-256-GCM (spec §7).</summary>
public sealed record VerificationCodeV1(string Email, string Purpose, string EncryptedCode, DateTimeOffset ExpiresAt);
