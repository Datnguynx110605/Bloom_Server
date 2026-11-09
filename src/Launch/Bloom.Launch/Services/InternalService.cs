using Bloom.Launch.Context;
using Bloom.Launch.Contracts;
using Bloom.Launch.Errors;
using Grpc.Core;

namespace Bloom.Launch.Services;

/// <summary>01-launch §5.1 internal API (order calls it; no HTTP route, spec §6).</summary>
public sealed class InternalService : LaunchInternal.LaunchInternalBase
{
    public override Task<HoldAllocationResponse> HoldAllocation(HoldAllocationRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireCustomer();
        Validate(request);
        throw new NotImplementedException("HoldAllocation's transaction (01-launch §6.5) is not built yet.");
    }

    private static void Validate(HoldAllocationRequest r)
    {
        if (!Guid.TryParseExact(r.OrderId, "D", out _)) throw LaunchErrors.Validation("order_id", "order_id must be a UUID.");
        if (r.Lines.Count == 0) throw LaunchErrors.Validation("lines", "lines must not be empty.");
        var seen = new HashSet<Guid>();
        foreach (var line in r.Lines)
        {
            if (!Guid.TryParseExact(line.SkuId, "D", out var sku)) throw LaunchErrors.Validation("lines.sku_id", "sku_id must be a UUID.");
            if (!seen.Add(sku)) throw LaunchErrors.Validation("lines.sku_id", "A sku_id appears twice.");
            if (line.Quantity < 1) throw LaunchErrors.Validation("lines.quantity", "quantity must be at least 1.");
        }
    }
}
