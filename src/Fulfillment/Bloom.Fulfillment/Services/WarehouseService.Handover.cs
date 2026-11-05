using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Context;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Bloom.Fulfillment.Errors;
using Bloom.Fulfillment.V1;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Shipment = Bloom.Fulfillment.Data.Shipment;

namespace Bloom.Fulfillment.Services;

/// <summary>§6.2 step 4 (spec §7): the label scan.</summary>
public sealed partial class WarehouseService
{
    /// <summary>
    /// Outbound: guarded task <c>packed → handed_over</c>, then shipment <c>label_ready → handed_over</c>, in the lock order (pack_tasks → shipments) that
    /// <c>CancelPackTask</c> uses, so a Support cancel and a scan never deadlock and exactly one wins (FR-01, F-01). Zero rows: roll back and map (R9).
    /// Ship-back: the shipment only (RET-R08). Return labels are not handed over here.
    /// </summary>
    public override async Task<V1.Shipment> HandOver(HandOverRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Packers);
        var ct = context.CancellationToken;
        Guid? shipmentId = null;
        string? tracking = null;
        switch (request.TargetCase)
        {
            case HandOverRequest.TargetOneofCase.ShipmentId: shipmentId = Inputs.Id(request.ShipmentId, "shipment_id"); break;
            case HandOverRequest.TargetOneofCase.TrackingNumber:
                tracking = request.TrackingNumber.Trim();
                if (tracking.Length == 0) throw FulfillmentErrors.Validation("tracking_number", "The tracking number is empty.");
                break;
            default: throw FulfillmentErrors.Validation("target", "Give a shipment_id or a tracking_number.");
        }

        var shipment = await db.Shipments.AsNoTracking().Where(s => shipmentId != null ? s.Id == shipmentId : s.TrackingNumber == tracking).SingleOrDefaultAsync(ct)
            ?? throw FulfillmentErrors.NotFound("The shipment");
        if (shipment.Status == ShipmentStatuses.HandedOver) return Views.Shipment(shipment);
        if (shipment.Direction == Directions.Return) throw FulfillmentErrors.Error(FulfillmentErrors.InvalidState, "A return label is not handed over by the warehouse.");

        var now = clock.GetUtcNow();
        var task = shipment.PackTaskId is { } taskId ? await db.PackTasks.AsNoTracking().SingleAsync(p => p.Id == taskId, ct) : null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var won = task is null || await db.Database.ExecuteSqlAsync($"""
            UPDATE pack_tasks SET status = {TaskStatuses.HandedOver}, version = version + 1, updated_at = {now}
            WHERE id = {task.Id} AND status = {TaskStatuses.Packed}
            """, ct) == 1;
        won = won && await db.Database.ExecuteSqlAsync($"""
            UPDATE shipments SET status = {ShipmentStatuses.HandedOver}, handed_over_at = {now}, version = version + 1
            WHERE id = {shipment.Id} AND status = {ShipmentStatuses.LabelReady}
            """, ct) == 1;
        if (!won)
        {
            await tx.RollbackAsync(ct);
            var current = await db.Shipments.AsNoTracking().SingleAsync(s => s.Id == shipment.Id, ct);
            return current.Status switch
            {
                ShipmentStatuses.HandedOver => Views.Shipment(current), // a concurrent double scan
                ShipmentStatuses.LabelPending => throw FulfillmentErrors.Error(FulfillmentErrors.LabelNotReady, "The label has not been bought yet."),
                _ => throw FulfillmentErrors.Error(FulfillmentErrors.InvalidState, $"The shipment is {current.Status}, not label_ready."),
            };
        }

        if (task is not null)
        {
            var lines = await db.PackTaskLines.AsNoTracking().Where(l => l.TaskId == task.Id && l.Kind == LineKinds.Product).OrderBy(l => l.LineNo).ToListAsync(ct);
            var serials = (await ScannedAsync(task.Id, ct)).ToLookup(s => s.LineNo, s => s.Serial);
            outbox.PublishEvent(FulfillmentMessages.ShipmentShipped, new ShipmentShippedV1(
                task.OrderId, task.RefId, lines.Select(l => new ShippedLineV1(l.SkuId, l.Quantity, serials[l.LineNo].ToList())).ToList(), shipment.TrackingNumber!));
        }
        audit.Record(caller.UserId, caller.Role!, "fulfillment.hand-over", "shipment", shipment.Id.ToString(), new { orderId = shipment.OrderId, trackingNumber = shipment.TrackingNumber });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (task is not null) metrics.HandedOver(shipment.Service, (now - task.CreatedAt).TotalSeconds); // R21
        return Views.Shipment(await db.Shipments.AsNoTracking().SingleAsync(s => s.Id == shipment.Id, ct));
    }
}
