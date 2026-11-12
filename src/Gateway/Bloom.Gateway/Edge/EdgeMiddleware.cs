using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using StackExchange.Redis;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

namespace Bloom.Gateway;

/// <summary>Steps 4 to 8 of the edge pipeline (LLD 07 §3); later steps are added here in that order.</summary>
public sealed class EdgeMiddleware(RequestDelegate next, TokenValidator validator, EdgeStore store, FallbackLimiters fallback, GatewayMetrics metrics, IReadOnlyDictionary<string, RateLimitPolicy> policies, IConfiguration configuration)
{
    // One EdgeRoute per route config instance; a config reload yields new instances and the old entries are collected.
    private readonly ConditionalWeakTable<RouteConfig, EdgeRoute> _parsed = [];
    private readonly HashSet<string> _allowedOrigins = new(configuration.GetSection("Gateway:AllowedOrigins").Get<string[]>() ?? [], StringComparer.OrdinalIgnoreCase);
    private readonly ConditionalWeakTable<RouteConfig, EdgeRoute>.CreateValueCallback _parse = config => EdgeRoute.From(config, policies);

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<RouteModel>() is not { } model)
        {
            // No route, or ASP.NET's own 405 endpoint: both are "no such route". Health endpoints are not routes.
            if (endpoint is RouteEndpoint { RoutePattern.RawText: "/healthz" or "/readyz" })
                await next(context);
            else
                await GatewayErrors.WriteAsync(context, GatewayErrors.RouteNotFound);
            return;
        }

        var route = _parsed.GetValue(model.Config, _parse);
        context.Features.Set(route);

        // Step 4b: a missing or foreign Origin on the refresh and hub routes is refused (CSRF defence, G-09, plan P5).
        if (route.CheckOrigin && !_allowedOrigins.Contains(context.Request.Headers.Origin.ToString()))
        {
            await GatewayErrors.WriteAsync(context, GatewayErrors.OriginNotAllowed);
            return;
        }
        var bodyLimit = context.Features.GetRequiredFeature<IHttpMaxRequestBodySizeFeature>();
        if (route.MaxBodyBytes is { } max)
            bodyLimit.MaxRequestBodySize = max; // Kestrel enforces it for chunked bodies
        if (context.Request.ContentLength > bodyLimit.MaxRequestBodySize)
        {
            // A declared length over the limit is refused here, before YARP sends headers upstream (Kestrel would only throw on the first body read).
            await GatewayErrors.WriteAsync(context, GatewayErrors.PayloadTooLarge);
            return;
        }

        // Step 5: JWT validation. Public and None routes ignore Authorization entirely (spec §7); the proxy never forwards it.
        var authorization = context.Request.Headers.Authorization.ToString();
        context.Request.Headers.Remove("Authorization");
        Caller? caller = null;
        if (route.Auth is not (AuthMode.Public or AuthMode.None))
        {
            // No Authorization header on a public-optional-user route is an anonymous caller; one that isn't Bearer is TOKEN_INVALID everywhere (LLD 07 §5.3).
            if (authorization.Length == 0)
            {
                if (route.Auth != AuthMode.PublicOptionalUser)
                {
                    await FailAsync(context, GatewayErrors.TokenMissing);
                    return;
                }
            }
            else if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                || (caller = await validator.ValidateAsync(authorization["Bearer ".Length..].Trim())) is null)
            {
                await FailAsync(context, GatewayErrors.TokenInvalid);
                return;
            }
        }

        // Step 6: revocation and rate limits, one Redis call (§6.2). Anonymous callers of public-optional-user routes get the global limit only.
        var routePolicy = route.Auth == AuthMode.PublicOptionalUser && caller is null ? null : route.Policy;
        var clientIp = ClientIp(context);
        EdgeDecision decision;
        try
        {
            decision = await store.CheckAsync(caller, clientIp, routePolicy, context.Request.Headers["x-bloom-request-id"].ToString());
            metrics.SetRedisDegraded(false);
        }
        catch (Exception e) when (IsRedisOutage(e))
        {
            // Redis is unreachable or not answering (§6.2): staff fail closed, everyone else gets per-pod limits and no revocation check.
            metrics.SetRedisDegraded(true);
            if (caller is { IsStaff: true })
            {
                await GatewayErrors.WriteAsync(context, GatewayErrors.AuthStoreUnavailable);
                return;
            }
            decision = fallback.Check(caller, clientIp, routePolicy);
        }
        if (decision.Revoked)
        {
            await FailAsync(context, GatewayErrors.TokenRevoked);
            return;
        }
        if (!decision.Allowed)
        {
            metrics.RateLimited(decision.DeniedPolicy!);
            await GatewayErrors.WriteAsync(context, GatewayErrors.RateLimited, decision.RetryAfter);
            return;
        }

        // Step 7: role check.
        if (caller is not null && !RoleAllowed(route, caller))
        {
            await GatewayErrors.WriteAsync(context, GatewayErrors.RoleNotAllowed);
            return;
        }

        // Step 8: trusted identity context (§4.3). Client x-bloom-* headers were already removed.
        if (caller is not null)
        {
            context.Features.Set(caller);
            var headers = context.Request.Headers;
            headers["x-bloom-user-id"] = caller.UserId.ToString("D");
            headers["x-bloom-user-type"] = caller.UserType;
            if (caller.IsStaff)
                headers["x-bloom-role"] = caller.Role;
            headers["x-bloom-auth-time"] = caller.AuthTime.ToString();
        }
        await next(context);
    }

    /// <summary>Redis is unreachable, not answering, or mid-failover (§6.2). Any other server error (a Lua error, WRONGTYPE) means Redis answered, so it is a 500.</summary>
    internal static bool IsRedisOutage(Exception e) => e switch
    {
        RedisServerException s => OutageCodes.Any(c => s.Message.StartsWith(c, StringComparison.Ordinal)),
        _ => e is RedisException or TimeoutException, // RedisTimeoutException is a TimeoutException, not a RedisException
    };

    private static readonly string[] OutageCodes = ["LOADING", "READONLY", "MASTERDOWN", "CLUSTERDOWN", "TRYAGAIN", "BUSY", "MISCONF", "OOM", "NOREPLICAS"];

    private static bool RoleAllowed(EdgeRoute route, Caller caller) => route.Auth switch
    {
        AuthMode.Customer => !caller.IsStaff,
        AuthMode.Staff => caller.IsStaff && route.Roles.Contains(caller.Role!),
        _ => true, // any-user and public-optional-user accept either type
    };

    private static string ClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } ip ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : "unknown";

    private Task FailAsync(HttpContext context, GatewayError error)
    {
        metrics.AuthFailure(error.Reason);
        return GatewayErrors.WriteAsync(context, error);
    }
}
