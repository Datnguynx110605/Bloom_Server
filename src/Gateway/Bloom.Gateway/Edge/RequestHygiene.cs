using System.Diagnostics;
using Yarp.ReverseProxy.Model;

namespace Bloom.Gateway;

/// <summary>
/// Steps 1 and 2 of the edge pipeline (LLD 07 §3): a fresh request ID, no client <c>x-bloom-*</c> headers (GW-02),
/// response hardening headers, and the request metrics of §10.
/// </summary>
public sealed class RequestHygiene(RequestDelegate next, GatewayMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var start = Stopwatch.GetTimestamp();
        var headers = context.Request.Headers;
        foreach (var name in headers.Keys.Where(k => k.StartsWith("x-bloom-", StringComparison.OrdinalIgnoreCase)).ToList())
            headers.Remove(name);
        headers.Remove("X-Original-For"); // ForwardedHeadersMiddleware's record of the hop; not for services (plan P9)
        headers["x-bloom-request-id"] = Guid.NewGuid().ToString("D");

        context.Response.OnStarting(static state =>
        {
            var response = ((HttpContext)state).Response.Headers;
            response.StrictTransportSecurity = "max-age=31536000";
            response.XContentTypeOptions = "nosniff";
            response["Referrer-Policy"] = "no-referrer";
            response.Remove("Server");
            response.Remove("X-Powered-By");
            response.Remove("X-AspNet-Version");
            return Task.CompletedTask;
        }, context);

        var status = StatusCodes.Status500InternalServerError; // what Kestrel answers if next throws
        try
        {
            await next(context);
            status = context.Response.StatusCode;
        }
        finally
        {
            var route = context.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.RouteId ?? "none";
            metrics.Request(route, status, Stopwatch.GetElapsedTime(start).TotalSeconds,
                context.Features.Get<UpstreamElapsed>()?.Seconds);
        }
    }
}
