using Bloom.Shared.Errors;
using Grpc.Core;

namespace Bloom.Inventory.Context;

/// <summary>The identity the gateway forwards in <c>x-bloom-*</c> headers (LLD 07 §4.3), plan R7.</summary>
public sealed record Caller(Guid UserId, string UserType, string? Role)
{
    public static readonly string[] StockViewers = ["warehouse", "catalog_manager", "support", "admin"];
    public static readonly string[] LedgerReaders = ["warehouse", "admin"];
    public static readonly string[] StockWriters = ["warehouse", "admin"];
    public static readonly string[] SerialReaders = ["warehouse", "support", "admin"];

    /// <exception cref="InvalidOperationException">A header is missing: the gateway always sets them on protected methods.</exception>
    public static Caller From(ServerCallContext ctx) =>
        TryFrom(ctx) ?? throw new InvalidOperationException("x-bloom-user-id is missing: the gateway must set it on every authenticated call.");

    /// <summary>Null when the request carries no <c>x-bloom-user-id</c> (a call from another service).</summary>
    public static Caller? TryFrom(ServerCallContext ctx)
    {
        var id = ctx.RequestHeaders.GetValue("x-bloom-user-id");
        if (id is null) return null;
        return new(
            Guid.TryParse(id, out var userId) ? userId : throw new InvalidOperationException("x-bloom-user-id is not a UUID."),
            ctx.RequestHeaders.GetValue("x-bloom-user-type") ?? throw new InvalidOperationException("x-bloom-user-type is missing."),
            ctx.RequestHeaders.GetValue("x-bloom-role"));
    }

    public Caller RequireStaff(params string[] roles) =>
        UserType == "staff" && Role is not null && roles.Contains(Role) ? this : throw Denied("Your role cannot do this.");

    private static RpcException Denied(string message) =>
        BloomErrors.Create(StatusCode.PermissionDenied, BloomErrors.RoleNotAllowed, message);
}
