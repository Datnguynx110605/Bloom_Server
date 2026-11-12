namespace Bloom.Identity.Contracts;

/// <summary>Message types of 06-identity §5.2.</summary>
public static class IdentityMessages
{
    public const string CustomerRegistered = "identity.customer-registered.v1";
    public const string ContactChanged = "identity.contact-changed.v1";
    public const string AccountDeleted = "identity.account-deleted.v1";
    public const string TokensRevoked = "identity.tokens-revoked.v1";
    public const string VerificationCode = "identity.verification-code.v1";
    public const string StaffInvited = "identity.staff-invited.v1";
    public const string StaffActionRecorded = "identity.staff-action-recorded.v1";
}
