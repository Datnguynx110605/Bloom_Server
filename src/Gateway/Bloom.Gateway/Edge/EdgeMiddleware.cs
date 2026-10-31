using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Routing;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

namespace Bloom.Gateway;

/// <summary>Steps 4 to 8 of the edge pipeline (LLD 07 §3); later steps are added here in that order.</summary>
public sealed class EdgeMiddleware(RequestDelegate next)
{
    // One EdgeRoute per route config instance; a config reload yields new instances and the old entries are collected.
    private static readonly ConditionalWeakTable<RouteConfig, EdgeRoute> Parsed = [];

    public Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<RouteModel>() is not { } model)
        {
            // No route, or ASP.NET's own 405 endpoint: both are "no such route". Health endpoints are not routes.
            return endpoint is RouteEndpoint { RoutePattern.RawText: "/healthz" or "/readyz" }
                ? next(context)
                : GatewayErrors.WriteAsync(context, GatewayErrors.RouteNotFound);
        }

        context.Features.Set(Parsed.GetValue(model.Config, EdgeRoute.From));
        context.Request.Headers.Remove("Authorization");
        return next(context);
    }
}
