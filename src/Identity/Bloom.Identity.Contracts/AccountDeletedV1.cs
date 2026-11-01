namespace Bloom.Identity.Contracts;

/// <summary>Published by identity when a customer deletes the account (06-identity §6.8).</summary>
public sealed record AccountDeletedV1(Guid AccountId);
