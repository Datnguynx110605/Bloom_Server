namespace Bloom.Payment.Domain;

/// <summary>Retry delays of the refund executor (04-payment §6.3).</summary>
internal static class Backoff
{
    /// <summary>Delay after transient failure n (1-based); the 5th failure fails the refund (spec §7).</summary>
    internal static readonly TimeSpan[] Schedule = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];
}
