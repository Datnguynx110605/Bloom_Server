using System.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Model;

namespace Bloom.Gateway;

/// <summary>
/// Steps 1 and 2 of the edge pipeline (LLD 07 §3): a fresh request ID, no client <c>x-bloom-*</c> headers (GW-02),
/// response hardening headers, and the request metrics of §10.
/// </summary>
public sealed class RequestHygiene(RequestDelegate next, GatewayMetrics metrics)
{
    /// <summary>
    /// Runs before <c>UseForwardedHeaders</c>: a client that isn't a trusted proxy (<c>Gateway:TrustedProxies</c>) must not feed the services
    /// its own <c>X-Forwarded-*</c> (LLD 07 §6.4, D14). They are replaced with the connection's values, or dropped when there is no such value.
    /// </summary>
    public static Task ReplaceUntrustedForwarded(HttpContext context, RequestDelegate next)
    {
        var networks = context.RequestServices.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value.KnownIPNetworks;
        var remote = context.Connection.RemoteIpAddress;
        remote = remote is { IsIPv4MappedToIPv6: true } ? remote.MapToIPv4() : remote;
        if (remote is null || !networks.Any(n => n.Contains(remote)))
        {
            var headers = context.Request.Headers;
            foreach (var name in headers.Keys.Where(k => k.StartsWith("x-forwarded-", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var actual = name.ToLowerInvariant() switch
                {
                    "x-forwarded-for" => remote?.ToString(),
                    "x-forwarded-proto" => context.Request.Scheme,
                    "x-forwarded-host" => context.Request.Host.Value,
                    _ => null,
                };
                if (actual is null)
                    headers.Remove(name);
                else
                    headers[name] = actual;
            }
        }
        return next(context);
    }

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
