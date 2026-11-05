using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Errors;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Bloom.Fulfillment.Services;

/// <summary>05-fulfillment §5.1 internal API (order calls it; no HTTP route).</summary>
public sealed class InternalService : FulfillmentInternal.FulfillmentInternalBase
{
    public override Task<Empty> CancelPackTask(CancelPackTaskRequest request, ServerCallContext context)
    {
        Inputs.Id(request.OrderId, "order_id");
        if (request.By is not ("customer" or "support"))
            throw FulfillmentErrors.Validation("by", "by must be 'customer' or 'support'.");
        throw new NotImplementedException("CancelPackTask arrives with the packing task (plan Task 3).");
    }
}
