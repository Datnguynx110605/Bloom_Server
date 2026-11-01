namespace Bloom.Identity.Contracts;

/// <summary>Published to notification (email only). <c>Email</c> is the normalized address; the raw invite token appears only in <c>InviteUrl</c> (§5.2).</summary>
public sealed record StaffInvitedV1(string Email, string Role, string InviteUrl);
