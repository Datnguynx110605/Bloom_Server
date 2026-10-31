using System.Diagnostics.Metrics;

namespace Bloom.Gateway;

/// <summary>Instruments of LLD 07 §10.</summary>
public sealed class GatewayMetrics
{
    public const string MeterName = "Bloom.Gateway";

    private readonly Counter<long> _requests, _authFailures, _jwksRefreshes;
    private readonly Histogram<double> _overhead, _upstream;

    public GatewayMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _requests = meter.CreateCounter<long>("gateway_requests_total");
        _overhead = meter.CreateHistogram<double>("gateway_overhead_seconds", "s");
        _upstream = meter.CreateHistogram<double>("gateway_upstream_seconds", "s");
        _authFailures = meter.CreateCounter<long>("gateway_auth_failures_total");
        _jwksRefreshes = meter.CreateCounter<long>("jwks_refresh_total");
    }

    /// <param name="reason"><c>TOKEN_MISSING</c>, <c>TOKEN_INVALID</c> or <c>TOKEN_REVOKED</c> (plan P8).</param>
    public void AuthFailure(string reason) => _authFailures.Add(1, new KeyValuePair<string, object?>("reason", reason));

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
