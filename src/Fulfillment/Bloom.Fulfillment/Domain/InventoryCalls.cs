using Bloom.Fulfillment.Errors;
using Bloom.Inventory.Contracts;
using Grpc.Core;

namespace Bloom.Fulfillment.Domain;

/// <summary>
/// Calls to inventory (plan R11): 5 s deadline, the staff caller's <c>x-bloom-*</c> headers forwarded for inventory's audit. Inventory's <c>bloom</c> errors reach our caller
/// with the same status and reason (the exception travels on with its trailers); anything else (<c>UNAVAILABLE</c>, <c>DEADLINE_EXCEEDED</c>) is not mapped either.
/// </summary>
public sealed class InventoryCalls(InventoryPacking.InventoryPackingClient packing, InventoryStock.InventoryStockClient stock)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public async Task AssignAsync(Guid refId, Guid skuId, string serial, ServerCallContext ctx) =>
        await packing.AssignSerialAsync(new() { RefId = refId.ToString(), SkuId = skuId.ToString(), Serial = serial }, Options(ctx));

    public async Task UnassignAsync(Guid refId, string serial, ServerCallContext ctx) =>
        await packing.UnassignSerialAsync(new() { RefId = refId.ToString(), Serial = serial }, Options(ctx));

    public async Task ReplaceAsync(Guid refId, string oldSerial, string newSerial, ServerCallContext ctx) =>
        await packing.ReplaceSerialAsync(new() { RefId = refId.ToString(), OldSerial = oldSerial, NewSerial = newSerial }, Options(ctx));

    /// <summary>Runs inside the idempotency guard's provider (spec §7), which closes over the staff call. <c>SOLD_OUT</c> becomes <c>RESHIP_NO_STOCK</c>.</summary>
    public async Task CommitForReshipAsync(Guid reshipId, Guid orderId, IEnumerable<(Guid SkuId, int Quantity)> lines, ServerCallContext ctx)
    {
        var request = new CommitForReshipRequest { ReshipId = reshipId.ToString(), OrderId = orderId.ToString() };
        request.Lines.AddRange(lines.Select(l => new StockLine { SkuId = l.SkuId.ToString(), Quantity = l.Quantity }));
        try
        {
            await stock.CommitForReshipAsync(request, Options(ctx));
        }
        catch (RpcException e) when (e.GetRpcStatus()?.Details.Any(d => d.Is(Google.Rpc.ErrorInfo.Descriptor) && d.Unpack<Google.Rpc.ErrorInfo>().Reason == "SOLD_OUT") == true)
        {
            throw FulfillmentErrors.Error(FulfillmentErrors.ReshipNoStock, "Inventory has no stock for the reship.");
        }
    }

    private static CallOptions Options(ServerCallContext ctx)
    {
        var headers = new Metadata();
        foreach (var name in new[] { "x-bloom-user-id", "x-bloom-user-type", "x-bloom-role" })
            if (ctx.RequestHeaders.GetValue(name) is { } value) headers.Add(name, value);
        return new CallOptions(headers, DateTime.UtcNow + Deadline, ctx.CancellationToken);
    }
}
