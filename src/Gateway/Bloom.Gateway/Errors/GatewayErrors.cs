using Bloom.Shared.Errors;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;

namespace Bloom.Gateway;

public sealed record GatewayError(int HttpStatus, StatusCode Code, string Reason, string Message);

/// <summary>Gateway-originated errors, in the same JSON shape as transcoded gRPC errors (LLD 07 §5.3).</summary>
public static class GatewayErrors
{
    public static readonly GatewayError TokenMissing = new(401, StatusCode.Unauthenticated, "TOKEN_MISSING", "Authentication is required.");
    public static readonly GatewayError TokenInvalid = new(401, StatusCode.Unauthenticated, "TOKEN_INVALID", "The access token is invalid or expired.");
    public static readonly GatewayError TokenRevoked = new(401, StatusCode.Unauthenticated, "TOKEN_REVOKED", "The access token has been revoked.");
    public static readonly GatewayError RoleNotAllowed = new(403, StatusCode.PermissionDenied, BloomErrors.RoleNotAllowed, "Your account type or role cannot access this resource.");
    public static readonly GatewayError OriginNotAllowed = new(403, StatusCode.PermissionDenied, "ORIGIN_NOT_ALLOWED", "The request origin is not allowed.");
    public static readonly GatewayError RouteNotFound = new(404, StatusCode.NotFound, "ROUTE_NOT_FOUND", "No route matches this request.");
    public static readonly GatewayError PayloadTooLarge = new(413, StatusCode.InvalidArgument, "PAYLOAD_TOO_LARGE", "The request body is too large.");
    public static readonly GatewayError RateLimited = new(429, StatusCode.ResourceExhausted, "RATE_LIMITED", "Too many requests.");
    public static readonly GatewayError AuthStoreUnavailable = new(503, StatusCode.Unavailable, "AUTH_STORE_UNAVAILABLE", "The authentication store is unavailable.");
    public static readonly GatewayError UpstreamUnavailable = new(503, StatusCode.Unavailable, "UPSTREAM_UNAVAILABLE", "The service is unavailable.");
    public static readonly GatewayError UpstreamTimeout = new(504, StatusCode.DeadlineExceeded, "UPSTREAM_TIMEOUT", "The service did not respond in time.");

    private static readonly JsonFormatter Formatter = new(JsonFormatter.Settings.Default.WithTypeRegistry(TypeRegistry.FromMessages(ErrorInfo.Descriptor)));

    /// <param name="retryAfter">Sent as <c>Retry-After</c> in whole seconds, rounded up, at least 1 (plan P4).</param>
    public static Task WriteAsync(HttpContext context, GatewayError error, TimeSpan? retryAfter = null)
    {
        var status = new Google.Rpc.Status
        {
            Code = (int)error.Code,
            Message = error.Message,
            Details = { Any.Pack(new ErrorInfo { Reason = error.Reason, Domain = BloomErrors.Domain }) },
        };
        var response = context.Response;
        response.StatusCode = error.HttpStatus;
        response.ContentType = "application/json";
        if (retryAfter is { } wait)
            response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString();
        return response.WriteAsync(Formatter.Format(status));
    }
}
