namespace Bloom.Gateway;

public enum Algorithm { FixedWindow, SlidingWindow, TokenBucket }

public enum LimitKey { Ip, User }

/// <summary>One policy of LLD 07 §4.2. Fixed and sliding windows use <c>Limit</c> and <c>WindowSeconds</c>; the token bucket uses <c>Burst</c> and <c>PerMinute</c>.</summary>
public sealed record RateLimitPolicy(string Name, Algorithm Algorithm, LimitKey Key, int Limit, int WindowSeconds, int Burst, double PerMinute)
{
    /// <summary>Who the limit counts: the client IP, or the signed-in user.</summary>
    public string Subject(Caller? caller, string clientIp) => Key == LimitKey.Ip ? clientIp
        : caller?.UserId.ToString("D") ?? throw new InvalidOperationException($"Policy '{Name}' is per user but the request is anonymous.");

    /// <summary>Reads every <c>Gateway:RateLimits:&lt;name&gt;</c> section; the section name is the policy name.</summary>
    public static IReadOnlyDictionary<string, RateLimitPolicy> Bind(IConfiguration configuration) =>
        configuration.GetSection("Gateway:RateLimits").GetChildren().ToDictionary(s => s.Key, s => new RateLimitPolicy(
            s.Key,
            s.GetValue<Algorithm>("Algorithm"),
            s.GetValue<LimitKey>("Key"),
            s.GetValue<int>("Limit"),
            s.GetValue<int>("WindowSeconds"),
            s.GetValue<int>("Burst"),
            s.GetValue<double>("PerMinute")));
}
