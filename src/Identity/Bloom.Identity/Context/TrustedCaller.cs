using Bloom.Identity.Errors;
using Bloom.Shared.Errors;
using Grpc.Core;

namespace Bloom.Identity.Context;

/// <summary>The identity the gateway forwards in <c>x-bloom-*</c> headers (LLD 07 §4.3); <c>auth_time</c> is Unix seconds.</summary>
public sealed record TrustedCaller(Guid UserId, string UserType, string? Role, DateTimeOffset AuthTime)
{
    private static readonly TimeSpan ReauthWindow = TimeSpan.FromMinutes(5); // ID-06, plan I18

    /// <exception cref="InvalidOperationException">A header is missing: the gateway always sets them (plan I31).</exception>
    public static TrustedCaller From(ServerCallContext ctx)
    {
        string Header(string name) => ctx.RequestHeaders.GetValue(name)
            ?? throw new InvalidOperationException($"{name} is missing: the gateway must set it on every authenticated call.");

        return new(
            Guid.TryParse(Header("x-bloom-user-id"), out var id) ? id : throw new InvalidOperationException("x-bloom-user-id is not a UUID."),
            Header("x-bloom-user-type"),
            ctx.RequestHeaders.GetValue("x-bloom-role"),
            long.TryParse(Header("x-bloom-auth-time"), out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : throw new InvalidOperationException("x-bloom-auth-time is not Unix seconds."));
    }

    public TrustedCaller RequireCustomer() =>
        UserType == "customer" ? this : throw Denied("Customers only.");

    public TrustedCaller RequireAdmin() =>
        UserType == "staff" && Role == "admin" ? this : throw Denied("Admins only.");

    /// <summary><c>now − auth_time ≤ 5 min</c> passes.</summary>
    public TrustedCaller RequireRecentAuth(TimeProvider time) =>
        time.GetUtcNow() - AuthTime <= ReauthWindow
            ? this
            : throw IdentityErrors.Error(IdentityErrors.ReauthRequired, "Sign in again to continue.");

    private static RpcException Denied(string message) =>
        BloomErrors.Create(StatusCode.PermissionDenied, BloomErrors.RoleNotAllowed, message);
}
