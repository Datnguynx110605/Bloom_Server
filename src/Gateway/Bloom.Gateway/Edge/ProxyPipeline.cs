using System.Diagnostics;
using Microsoft.AspNetCore.Http.Timeouts;
using Yarp.ReverseProxy.Forwarder;

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
            await WriteForwarderErrorAsync(context);
        });
        pipeline.UseLoadBalancing();
    }

    /// <summary>Maps a failed forward to the gateway's error body (plan P15). Nothing is written once the response has started.</summary>
    private static Task WriteForwarderErrorAsync(HttpContext context)
    {
        if (context.Features.Get<IForwarderErrorFeature>() is not { } failure || context.Response.HasStarted)
            return Task.CompletedTask;
        // YARP reports a request-timeouts cancellation as RequestCanceled, so the timeout token tells it from a client that went away.
        var timedOut = failure.Error == ForwarderError.RequestTimedOut
            || (failure.Error == ForwarderError.RequestCanceled && context.Features.Get<IHttpRequestTimeoutFeature>()?.RequestTimeoutToken.IsCancellationRequested == true);
        return failure.Error switch
        {
            _ when timedOut => GatewayErrors.WriteAsync(context, GatewayErrors.UpstreamTimeout),
            ForwarderError.RequestBodyClient when IsBodyTooLarge(failure.Exception) => GatewayErrors.WriteAsync(context, GatewayErrors.PayloadTooLarge),
            ForwarderError.RequestCanceled => Task.CompletedTask, // the client is gone
            _ => GatewayErrors.WriteAsync(context, GatewayErrors.UpstreamUnavailable),
        };
    }

    // The 413 sits under the forwarder's AggregateException and an IOException.
    private static bool IsBodyTooLarge(Exception? error) => error switch
    {
        BadHttpRequestException bad => bad.StatusCode == StatusCodes.Status413PayloadTooLarge,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsBodyTooLarge),
        { InnerException: { } inner } => IsBodyTooLarge(inner),
        _ => false,
    };
}
