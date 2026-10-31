using System.Diagnostics;

namespace Bloom.Gateway;

/// <summary>How long the forward took, as seen by the proxy pipeline; set on <see cref="HttpContext.Features"/> for the metrics.</summary>
public sealed record UpstreamElapsed(double Seconds);

/// <summary>The YARP proxy pipeline (LLD 07 §3 step 9).</summary>
public static class ProxyPipeline
{
    public static void Configure(IReverseProxyApplicationBuilder pipeline)
    {
        pipeline.Use(async (context, next) =>
        {
            var start = Stopwatch.GetTimestamp();
            try { await next(); }
            finally { context.Features.Set(new UpstreamElapsed(Stopwatch.GetElapsedTime(start).TotalSeconds)); }
        });
        pipeline.UseLoadBalancing();
    }
}
