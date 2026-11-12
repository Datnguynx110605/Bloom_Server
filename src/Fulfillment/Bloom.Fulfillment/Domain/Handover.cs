using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Data;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Domain;

/// <summary>
/// The HandOver step of §6.2 step 4, shared by the warehouse scan and by a carrier scan that implies it (§6.3, D32). The caller is in a transaction and, for an outbound shipment
/// with a task, holds the task's row lock before the shipment's (lock order: pack_tasks → shipments).
/// </summary>
public sealed class Handover(FulfillmentDbContext db, OutboxWriter outbox)
{
    /// <summary>
    /// Guarded task <c>packed → handed_over</c>, then shipment <c>label_ready → handed_over</c> at <paramref name="now"/>, and <c>shipment-shipped</c> for an outbound task.
    /// Returns false, with nothing staged, when either guard hits no row. <paramref name="task"/> is null for a ship-back.
    /// </summary>
    public async Task<bool> ApplyAsync(Shipment shipment, PackTask? task, DateTimeOffset now, CancellationToken ct)
    {
        var won = task is null || await db.Database.ExecuteSqlAsync($"""
            UPDATE pack_tasks SET status = {TaskStatuses.HandedOver}, version = version + 1, updated_at = {now}
            WHERE id = {task.Id} AND status = {TaskStatuses.Packed}
            """, ct) == 1;
        won = won && await db.Database.ExecuteSqlAsync($"""
            UPDATE shipments SET status = {ShipmentStatuses.HandedOver}, handed_over_at = {now}, version = version + 1
            WHERE id = {shipment.Id} AND status = {ShipmentStatuses.LabelReady}
            """, ct) == 1;
        if (!won) return false;

        if (task is not null)
        {
            var lines = await db.PackTaskLines.AsNoTracking().Where(l => l.TaskId == task.Id && l.Kind == LineKinds.Product).OrderBy(l => l.LineNo).ToListAsync(ct);
            var serials = (await db.PackTaskSerials.AsNoTracking().Where(s => s.TaskId == task.Id).OrderBy(s => s.Seq).ToListAsync(ct)).ToLookup(s => s.LineNo, s => s.Serial);
            outbox.PublishEvent(FulfillmentMessages.ShipmentShipped, new ShipmentShippedV1(
                task.OrderId, task.RefId, lines.Select(l => new ShippedLineV1(l.SkuId, l.Quantity, serials[l.LineNo].ToList())).ToList(), shipment.TrackingNumber!));
        }
        return true;
    }
}
