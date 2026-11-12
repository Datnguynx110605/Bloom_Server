namespace Bloom.Identity.Data;

// Column types, constraints and indexes are in IdentityDbContext; LLD 06-identity §4.1.

public static class AccountTypes
{
    public const string Customer = "customer";
    public const string Staff = "staff";
}

public static class AccountStatuses
{
    public const string Active = "active";
    public const string Deactivated = "deactivated";
    public const string Deleted = "deleted";
}

public static class StaffRoles
{
    public const string Admin = "admin";
    public const string CatalogManager = "catalog_manager";
    public const string Warehouse = "warehouse";
    public const string Support = "support";

    public static readonly string[] All = [Admin, CatalogManager, Warehouse, Support];
}

public static class CodePurposes
{
    public const string SignUp = "signup";
    public const string ResetPassword = "reset_password";
    public const string ChangeEmail = "change_email";
}

public static class RefreshStatuses
{
    public const string Active = "active";
    public const string Rotated = "rotated";
    public const string Revoked = "revoked";
}

public class Account
{
    public Guid Id { get; set; }
    public required string Type { get; set; }
    public required string EmailNormalized { get; set; }
    public string? EmailDisplay { get; set; }
    public string? PasswordHash { get; set; }
    public string? GoogleSub { get; set; }
    public string? Name { get; set; }
    public string? Role { get; set; }
    public byte[]? TotpSecretEnc { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? TokensValidAfter { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastSignInAt { get; set; }
}

public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public Guid AccountId { get; set; }
    public required byte[] TokenHash { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset AuthTime { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public string? UserAgent { get; set; }
    public string? Ip { get; set; }
}

public class VerificationCode
{
    public Guid Id { get; set; }
    public required string Purpose { get; set; }
    public required string TargetEmail { get; set; }
    public Guid? AccountId { get; set; }
    public required byte[] CodeHmac { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

public class StaffInvitation
{
    public Guid Id { get; set; }
    public required string EmailNormalized { get; set; }
    public required string Role { get; set; }
    public required byte[] TokenHash { get; set; }
    public Guid InvitedBy { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
}

public class Address
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string? Label { get; set; }
    public required string Recipient { get; set; }
    public required string Line1 { get; set; }
    public string? Line2 { get; set; }
    public required string City { get; set; }
    public required string State { get; set; }
    public required string Zip { get; set; }
    public string? Phone { get; set; }
    public bool IsDefault { get; set; }
    public long Version { get; set; } = 1;
}

public class AuditEntry
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid ActorId { get; set; }
    public required string ActorRole { get; set; }
    public required string Service { get; set; }
    public required string Action { get; set; }
    public required string TargetType { get; set; }
    public required string TargetId { get; set; }
    public required string Details { get; set; }
}
