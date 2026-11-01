namespace Bloom.Identity.Contracts;

/// <summary>Published when a customer account is created; <c>Email</c> is the display form (plan I20).</summary>
public sealed record CustomerRegisteredV1(Guid AccountId, string Email, string Name);
