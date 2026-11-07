using System.Text.Json;
using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Context;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Bloom.Fulfillment.Errors;
using Bloom.Fulfillment.V1;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using PackTaskLine = Bloom.Fulfillment.Data.PackTaskLine;

namespace Bloom.Fulfillment.Services;

/// <summary>§6.5 (ORD-R12, R17, spec §4/§7): the warehouse records what came back from a failed delivery.</summary>
public sealed partial class WarehouseService
{
    private readonly record struct RtsUnitInput(string? Serial, Guid? SkuId, string Outcome);

    /// <summary>
    /// A confirmed <c>returned_to_sender</c> failure → <c>resolved_refund</c> in the guard's one transaction (plan R8). Lock order: shipments → delivery_failures → order_accounts →
    /// order_line_accounts → warranty_coverages. Units are checked against what shipped after the state (spec §7).
    /// </summary>
    public override async Task<V1.DeliveryFailure> RecordReturnedToSender(RecordReturnedToSenderRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Packers);
        var shipmentId = Inputs.Id(request.ShipmentId, "shipment_id");
        var units = ParseReturnedUnits(request);
        return await guard.ExecuteAsync(request, context, ct => ReturnedToSenderAsync(caller, shipmentId, units, ct));
    }

    /// <summary>Request-only checks (R10): at least one unit, each with an outcome of <c>sealed</c> or <c>failed</c> and a serial or a SKU.</summary>
    private static List<RtsUnitInput> ParseReturnedUnits(RecordReturnedToSenderRequest request)
    {
        if (request.Units.Count == 0) throw FulfillmentErrors.Validation("units", "List every unit that shipped.");
        return request.Units.Select(u =>
        {
            if (u.Outcome is not ("sealed" or "failed")) throw FulfillmentErrors.Validation("units", "The outcome of a unit is sealed or failed.");
            if (u.Serial.Length > 0) return new RtsUnitInput(Inputs.Serial(u.Serial, "units"), null, u.Outcome);
            return Guid.TryParseExact(u.SkuId, "D", out var sku)
                ? new RtsUnitInput(null, sku, u.Outcome)
                : throw FulfillmentErrors.Validation("units", "Give each unit a serial, or a sku_id when it has none.");
        }).ToList();
    }

    private async Task<V1.DeliveryFailure> ReturnedToSenderAsync(Caller caller, Guid shipmentId, List<RtsUnitInput> units, CancellationToken ct)
    {
        var (shipment, failure) = await failures.LockAsync(shipmentId, ct);
        if (failure is not { Kind: FailureKinds.ReturnedToSender, Status: FailureStatuses.Confirmed })
            throw FulfillmentErrors.Error(FulfillmentErrors.InvalidState, "The shipment has no confirmed returned-to-sender failure.");

        var task = await db.PackTasks.AsNoTracking().SingleAsync(p => p.Id == shipment.PackTaskId, ct);
        var lines = await db.PackTaskLines.AsNoTracking().Where(l => l.TaskId == task.Id && l.Kind == LineKinds.Product).ToListAsync(ct);
        var back = MatchShipped(units, lines, await ScannedAsync(task.Id, ct));
        outbox.SendCommand("inventory", FulfillmentMessages.ReceiveReturnedToSender, new ReceiveReturnedToSenderV1(task.RefId, back));
        await refunds.ResolveFailureAsync(shipment.OrderId, shipmentId, FailureKinds.ReturnedToSender, caller.UserId, JsonSerializer.Serialize(back, JsonSerializerOptions.Web), ct);
        audit.Record(caller.UserId, caller.Role!, "fulfillment.record-returned-to-sender", "shipment", shipmentId.ToString(), new { orderId = shipment.OrderId });
        return await Views.FailureAsync(db, shipmentId, ct);
    }

    /// <summary>
    /// The units must be exactly what shipped (spec §7): each shipped serial once, and for the other product lines as many units per SKU as shipped. The inventory units are
    /// the serials one by one and the others grouped by (SKU, outcome).
    /// </summary>
    private static List<ReturnedToSenderUnitV1> MatchShipped(List<RtsUnitInput> units, List<PackTaskLine> lines, List<PackTaskSerial> scanned)
    {
        var skuOfLine = lines.ToDictionary(l => l.LineNo, l => l.SkuId);
        var shipped = scanned.ToDictionary(s => s.Serial, s => skuOfLine[s.LineNo]);
        var loose = lines.Where(l => !l.Serialized).GroupBy(l => l.SkuId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var named = units.Where(u => u.Serial is not null).ToList();
        var unnamed = units.Where(u => u.Serial is null).ToList();
        var counts = unnamed.GroupBy(u => u.SkuId!.Value).ToDictionary(g => g.Key, g => g.Count());

        if (!named.Select(u => u.Serial!).Order(StringComparer.Ordinal).SequenceEqual(shipped.Keys.Order(StringComparer.Ordinal))
            || counts.Count != loose.Count || loose.Any(l => counts.GetValueOrDefault(l.Key) != l.Value))
            throw FulfillmentErrors.Validation("units", "The units must be exactly what shipped: every serial once and every other unit by sku_id.");

        return named.Select(u => new ReturnedToSenderUnitV1(shipped[u.Serial!], u.Serial, 1, u.Outcome))
            .Concat(unnamed.GroupBy(u => (Sku: u.SkuId!.Value, u.Outcome)).Select(g => new ReturnedToSenderUnitV1(g.Key.Sku, null, g.Count(), g.Key.Outcome))).ToList();
    }
}
