using Bloom.Identity.Errors;
using Bloom.Order.Contracts;
using Grpc.Core;

namespace Bloom.Identity.Orders;

/// <summary>The order service's <c>HasUnsettledOrders</c> (03-order §5), 2 s deadline (spec §7).</summary>
public sealed class OrderCheck(OrderInternal.OrderInternalClient client)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);

    /// <exception cref="RpcException"><c>ORDER_SERVICE_UNAVAILABLE</c> on any gRPC failure, including the deadline.</exception>
    public async Task<bool> HasUnsettledOrdersAsync(Guid accountId)
    {
        try
        {
            // gRPC deadlines are system-clock instants, so not the app's TimeProvider.
            var reply = await client.HasUnsettledOrdersAsync(
                new HasUnsettledOrdersRequest { AccountId = accountId.ToString("D") }, deadline: DateTime.UtcNow + Deadline);
            return reply.HasUnsettledOrders;
        }
        catch (RpcException)
        {
            throw IdentityErrors.Error(IdentityErrors.OrderServiceUnavailable, "Orders cannot be checked right now. Try again later.");
        }
    }
}
