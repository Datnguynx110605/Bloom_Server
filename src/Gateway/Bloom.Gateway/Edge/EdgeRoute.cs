using Yarp.ReverseProxy.Configuration;

namespace Bloom.Gateway;

public enum AuthMode { Public, PublicOptionalUser, Customer, Staff, AnyUser, None }

/// <summary>A route's edge settings (LLD 07 §5.1), read from the YARP route <c>Metadata</c>.</summary>
public sealed record EdgeRoute(string RouteId, AuthMode Auth, IReadOnlySet<string> Roles, RateLimitPolicy? Policy, long? MaxBodyBytes, bool CheckOrigin)
{
    public static EdgeRoute From(RouteConfig route, IReadOnlyDictionary<string, RateLimitPolicy> policies)
    {
        var metadata = route.Metadata ?? new Dictionary<string, string>();
        var auth = metadata.GetValueOrDefault("Auth") switch
        {
            "public" => AuthMode.Public,
            "public-optional-user" => AuthMode.PublicOptionalUser,
            "customer" => AuthMode.Customer,
            "staff" => AuthMode.Staff,
            "any-user" => AuthMode.AnyUser,
            "none" => AuthMode.None,
            var other => throw new InvalidOperationException($"Route '{route.RouteId}' has unknown Auth '{other}'."),
        };
        var roles = (metadata.GetValueOrDefault("Roles") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new EdgeRoute(
            route.RouteId,
            auth,
            roles.ToHashSet(),
            metadata.GetValueOrDefault("Policy") is { } name
                ? policies.GetValueOrDefault(name) ?? throw new InvalidOperationException($"Route '{route.RouteId}' has unknown Policy '{name}'.")
                : null,
            metadata.TryGetValue("MaxBodyBytes", out var max) ? long.Parse(max) : null,
            metadata.GetValueOrDefault("CheckOrigin") == "true");
    }
}
