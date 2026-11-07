using Bloom.Order.Contracts;
using Bloom.Order.Errors;
using Grpc.Core;

namespace Bloom.Order.Services;

/// <summary>03-order §5.2 internal API (identity calls it; no HTTP route).</summary>
public sealed class InternalService : OrderInternal.OrderInternalBase
{
    // The query arrives with the monitor task; until then the request is only validated.
    public override Task<HasUnsettledOrdersResponse> HasUnsettledOrders(HasUnsettledOrdersRequest request, ServerCallContext context)
    {
        Inputs.Id(request.AccountId, "account_id");
        throw new NotImplementedException();
    }
}
