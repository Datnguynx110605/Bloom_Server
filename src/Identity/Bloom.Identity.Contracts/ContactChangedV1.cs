namespace Bloom.Identity.Contracts;

/// <summary>Published when a customer's email changes; <c>Email</c> is the display form (plan I20).</summary>
public sealed record ContactChangedV1(Guid AccountId, string Email, string Name);
