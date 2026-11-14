using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Errors;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Services;

/// <summary>05-fulfillment §5.1 internal API (order calls it; no HTTP route).</summary>
public sealed class InternalService(FulfillmentDbContext db) : FulfillmentInternal.FulfillmentInternalBase
{
    /// <summary>
    /// §6.1, plan R9, R15: guarded status updates in one transaction. Customer: <c>pending</c> → <c>cancelled</c>. Support: also <c>packing</c> and <c>packed</c>,
    /// and the task's label is taken away. No task yet → a <c>cancelled_before_arrival</c> marker (F-02). Zero rows: the status says why. No audit: order audits its own cancel.
    /// </summary>
    public override async Task<Empty> CancelPackTask(CancelPackTaskRequest request, ServerCallContext context)
    {
        var orderId = Inputs.Id(request.OrderId, "order_id");
        if (request.By is not ("customer" or "support"))
            throw FulfillmentErrors.Validation("by", "by must be 'customer' or 'support'.");
        var support = request.By == "support";
        string[] cancellable = support ? [TaskStatuses.Pending, TaskStatuses.Packing, TaskStatuses.Packed] : [TaskStatuses.Pending]; // the customer can only cancel before packing starts
        var ct = context.CancellationToken;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        while (true)
        {
            // Lock order: pack_tasks, then shipments.
            var cancelled = await db.Database.ExecuteSqlAsync($"""
                UPDATE pack_tasks SET status = {TaskStatuses.Cancelled}, version = version + 1, updated_at = now()
                WHERE ref_id = {orderId} AND status = ANY({cancellable})
                """, ct);
            if (cancelled == 1)
            {
                if (support)
                {
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE shipments
                        SET status = CASE WHEN status = {ShipmentStatuses.LabelReady} THEN {ShipmentStatuses.VoidPending} ELSE {ShipmentStatuses.Voided} END, version = version + 1
                        WHERE pack_task_id = (SELECT id FROM pack_tasks WHERE ref_id = {orderId})
                          AND status IN ({ShipmentStatuses.LabelPending}, {ShipmentStatuses.LabelReady})
                        """, ct);
                    // Last in the lock order. The released serials must be free for another order (ux_warranty_coverages_serial_open).
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE warranty_coverages SET status = {CoverageStatuses.Cancelled}
                        WHERE order_id = {orderId} AND status = {CoverageStatuses.PendingDelivery}
                        """, ct);
                }
                break;
            }

            var status = await db.PackTasks.AsNoTracking().Where(p => p.RefId == orderId).Select(p => p.Status).SingleOrDefaultAsync(ct);
            if (status is null)
            {
                // A racing insert (the marker, or the arriving order) makes this a no-op; the next round sees it.
                if (await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO pack_tasks (id, ref_id, order_id, kind, status, cancelled_before_arrival, version, created_at, updated_at)
                    VALUES ({Guid.CreateVersion7()}, {orderId}, {orderId}, {TaskKinds.Order}, {TaskStatuses.Cancelled}, true, 0, now(), now())
                    ON CONFLICT (ref_id) DO NOTHING
                    """, ct) == 1) break;
                continue;
            }
            if (status == TaskStatuses.Cancelled) break;
            throw status == TaskStatuses.HandedOver
                ? FulfillmentErrors.Error(FulfillmentErrors.AlreadyShipped, "The order has already been handed over to the carrier.")
                : FulfillmentErrors.Error(FulfillmentErrors.PackingStarted, "Packing has already started.");
        }
        await tx.CommitAsync(ct);
        return new Empty();
    }
}
