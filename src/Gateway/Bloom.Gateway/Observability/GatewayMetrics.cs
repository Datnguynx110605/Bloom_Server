using System.Diagnostics.Metrics;

namespace Bloom.Gateway;

/// <summary>Instruments of LLD 07 §10.</summary>
public sealed class GatewayMetrics
{
    public const string MeterName = "Bloom.Gateway";

    private readonly Counter<long> _requests, _authFailures, _rateLimited, _jwksRefreshes;
    private readonly Histogram<double> _overhead, _upstream;
    private readonly ILogger<GatewayMetrics> _logger;
    private long _redisDegraded;

    public GatewayMetrics(IMeterFactory meterFactory, ILogger<GatewayMetrics> logger)
    {
        _logger = logger;
        var meter = meterFactory.Create(MeterName);
        _requests = meter.CreateCounter<long>("gateway_requests_total");
        _overhead = meter.CreateHistogram<double>("gateway_overhead_seconds", "s");
        _upstream = meter.CreateHistogram<double>("gateway_upstream_seconds", "s");
        _authFailures = meter.CreateCounter<long>("gateway_auth_failures_total");
        _rateLimited = meter.CreateCounter<long>("gateway_rate_limited_total");
        _jwksRefreshes = meter.CreateCounter<long>("jwks_refresh_total");
        meter.CreateObservableGauge("gateway_redis_degraded", () => Volatile.Read(ref _redisDegraded));
    }

    /// <param name="reason"><c>TOKEN_MISSING</c>, <c>TOKEN_INVALID</c> or <c>TOKEN_REVOKED</c> (plan P8).</param>
    public void AuthFailure(string reason) => _authFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>1 while requests are served from the in-memory fallback because Redis is unreachable.</summary>
    public void SetRedisDegraded(bool degraded)
    {
        if (Interlocked.Exchange(ref _redisDegraded, degraded ? 1 : 0) == (degraded ? 1 : 0))
            return;
        if (degraded)
            _logger.LogWarning("Redis is unreachable; entering degraded mode (per-pod limits, no revocation check)");
        else
            _logger.LogInformation("Redis is reachable again; leaving degraded mode");
    }

    public void RateLimited(string policy) => _rateLimited.Add(1, new KeyValuePair<string, object?>("policy", policy));

    /// <param name="result"><c>success</c> or <c>error</c>.</param>
    public void JwksRefresh(string result) => _jwksRefreshes.Add(1, new KeyValuePair<string, object?>("result", result));

    /// <param name="route">YARP route ID, or <c>none</c> when nothing matched (plan P8).</param>
    /// <param name="upstreamSeconds">Null when the request was not proxied.</param>
    public void Request(string route, int status, double totalSeconds, double? upstreamSeconds)
    {
        _requests.Add(1, new("route", route), new("status", status.ToString()));
        _overhead.Record(totalSeconds - (upstreamSeconds ?? 0), new KeyValuePair<string, object?>("route", route));
        if (upstreamSeconds is { } upstream)
            _upstream.Record(upstream, new KeyValuePair<string, object?>("route", route));
    }
}
