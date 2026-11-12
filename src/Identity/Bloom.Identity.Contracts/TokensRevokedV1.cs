namespace Bloom.Identity.Contracts;

/// <summary>Published by identity when an account's tokens are revoked; <c>IssuedBefore</c> is Unix milliseconds.</summary>
public sealed record TokensRevokedV1(Guid AccountId, long IssuedBefore);
