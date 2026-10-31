using System.Threading.RateLimiting;

namespace Bloom.Gateway;

/// <summary>Per-pod in-memory limiters with the same algorithms and numbers as the Redis ones, used while Redis is down (LLD 07 §6.2).</summary>
public sealed class FallbackLimiters(IReadOnlyDictionary<string, RateLimitPolicy> policies) : IDisposable
{
    // One partitioned limiter per policy; the partition (subject) is created on first use and dropped when idle.
    private readonly Dictionary<string, PartitionedRateLimiter<string>> _limiters = policies.ToDictionary(p => p.Key, p => Create(p.Value));

    /// <summary>Global first, then the route policy; webhooks skip global. Same order and exemption as <see cref="EdgeStore"/>; revocation is not checked.</summary>
    public EdgeDecision Check(Caller? caller, string clientIp, RateLimitPolicy? routePolicy)
    {
        if (routePolicy?.Name != "webhook" && Denied(policies["global"], clientIp) is { } globalWait)
            return new(false, false, globalWait, "global");
        if (routePolicy is not null && Denied(routePolicy, routePolicy.Subject(caller, clientIp)) is { } routeWait)
            return new(false, false, routeWait, routePolicy.Name);
        return new(false, true, TimeSpan.Zero, null);
    }

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
            limiter.Dispose();
    }

    /// <returns>How long to wait when the policy refuses the request, else null.</returns>
    private TimeSpan? Denied(RateLimitPolicy policy, string subject)
    {
        using var lease = _limiters[policy.Name].AttemptAcquire(subject);
        if (lease.IsAcquired)
            return null;
        return lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromSeconds(1);
    }

    private static PartitionedRateLimiter<string> Create(RateLimitPolicy p) =>
        PartitionedRateLimiter.Create<string, string>(subject => p.Algorithm switch
        {
            Algorithm.FixedWindow => RateLimitPartition.GetFixedWindowLimiter(subject, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = p.Limit,
                Window = TimeSpan.FromSeconds(p.WindowSeconds),
                QueueLimit = 0,
            }),
            Algorithm.SlidingWindow => RateLimitPartition.GetSlidingWindowLimiter(subject, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = p.Limit,
                Window = TimeSpan.FromSeconds(p.WindowSeconds),
                SegmentsPerWindow = 60, // .NET has no exact log; 60 segments per window (plan P10)
                QueueLimit = 0,
            }),
            _ => RateLimitPartition.GetTokenBucketLimiter(subject, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = p.Burst,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = TimeSpan.FromSeconds(60 / p.PerMinute),
                AutoReplenishment = true,
                QueueLimit = 0,
            }),
        });
}
