using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Context;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Bloom.Fulfillment.Errors;
using Bloom.Fulfillment.Observability;
using Bloom.Fulfillment.V1;
using Bloom.Shared.Concurrency;
using Bloom.Shared.Idempotency;
using Bloom.Shared.Messaging;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using PackTask = Bloom.Fulfillment.Data.PackTask;
using PackTaskLine = Bloom.Fulfillment.Data.PackTaskLine;
using Shipment = Bloom.Fulfillment.Data.Shipment;

namespace Bloom.Fulfillment.Services;

/// <summary>05-fulfillment §3 component <c>Warehouse API</c>, packing (§6.2). Role first (plan R7), then validation (R10), then state (R9). Scans call inventory first, outside any transaction (spec §7).</summary>
public sealed partial class WarehouseService(
    FulfillmentDbContext db, ConcurrentUpdates updates, InventoryCalls inventory, Audit audit, OutboxWriter outbox, FulfillmentMetrics metrics, TimeProvider clock,
    IdempotencyGuard guard, RefundEngine refunds, Failures failures, Handover handover) : FulfillmentWarehouse.FulfillmentWarehouseBase
{
    public override async Task<ListPackTasksResponse> ListPackTasks(ListPackTasksRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.Packers);
        var (skip, take) = Inputs.Paging(request.Page, request.PageSize);
        if (request.HasStatus && !TaskStatuses.All.Contains(request.Status)) throw FulfillmentErrors.Validation("status", "Not a pack task status.");

        var tasks = db.PackTasks.AsNoTracking().Where(p => !p.CancelledBeforeArrival);
        if (request.HasStatus) tasks = tasks.Where(p => p.Status == request.Status);
        var page = await tasks.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).Skip(skip).Take(take).ToListAsync(context.CancellationToken);
        return new ListPackTasksResponse { Items = { await Views.PackTasksAsync(db, page, context.CancellationToken) } };
    }

    /// <summary>R9: versioned <c>pending → packing</c> under the task's row lock. <c>packing-started</c> only for order tasks (owner item 5).</summary>
    public override async Task<V1.PackTask> StartPacking(StartPackingRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Packers);
        var taskId = Inputs.Id(request.TaskId, "task_id");
        var ct = context.CancellationToken;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var task = await TaskAsync(taskId, TaskStatuses.Pending, forUpdate: true, ct);
        var now = clock.GetUtcNow();
        await updates.UpdateVersionedAsync("pack task", db.PackTasks.Where(p => p.Id == taskId && p.Status == TaskStatuses.Pending), request.Version,
            b => b.SetProperty(p => p.Status, TaskStatuses.Packing).SetProperty(p => p.UpdatedAt, now), FulfillmentErrors.NotFoundReason, ct);
        if (task.Kind == TaskKinds.Order) outbox.PublishEvent(FulfillmentMessages.PackingStarted, new PackingStartedV1(task.OrderId));
        audit.Record(caller.UserId, caller.Role!, "fulfillment.start-packing", "pack_task", taskId.ToString(), new { orderId = task.OrderId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await Views.PackTaskAsync(db, taskId, ct);
    }

    /// <summary>§6.2 step 1. The inventory call comes first; a retry after a lost local write repeats it (inventory answers ok for a serial already assigned to the ref) and finds no row.</summary>
    public override async Task<V1.PackTask> ScanSerial(ScanSerialRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Packers);
        var taskId = Inputs.Id(request.TaskId, "task_id");
        var serial = Inputs.Serial(request.Serial, "serial");
        var ct = context.CancellationToken;

        var task = await TaskAsync(taskId, TaskStatuses.Packing, forUpdate: false, ct);
        var line = await ScannableLineAsync(taskId, request.LineNo, ct);
        if (AlreadyScanned(await ScannedAsync(taskId, ct), line, serial)) return await Views.PackTaskAsync(db, taskId, ct);
        await inventory.AssignAsync(task.RefId, line.SkuId, serial, context);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await TaskAsync(taskId, TaskStatuses.Packing, forUpdate: true, ct); // the room on the line and the next seq are decided under the lock
        var scanned = await ScannedAsync(taskId, ct);
        if (!AlreadyScanned(scanned, line, serial))
        {
            db.PackTaskSerials.Add(new PackTaskSerial { TaskId = taskId, Serial = serial, LineNo = line.LineNo, Seq = scanned.Select(s => s.Seq).DefaultIfEmpty().Max() + 1, ScannedAt = clock.GetUtcNow() });
            audit.Record(caller.UserId, caller.Role!, "fulfillment.scan-serial", "pack_task", taskId.ToString(), new { lineNo = line.LineNo, serial });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await Views.PackTaskAsync(db, taskId, ct);
    }

    /// <summary>D29: with no local record of the serial inventory is still called for the order, and its own check decides (its error passes through).</summary>
    public override async Task<V1.PackTask> UnassignSerial(UnassignSerialRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Packers);
        var taskId = Inputs.Id(request.TaskId, "task_id");
        var serial = Inputs.Serial(request.Serial, "serial");
        var ct = context.CancellationToken;

        var task = await TaskAsync(taskId, TaskStatuses.Packing, forUpdate: false, ct);
        await inventory.UnassignAsync(task.RefId, serial, context);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await TaskAsync(taskId, TaskStatuses.Packing, forUpdate: true, ct);
        if (await db.Database.ExecuteSqlAsync($"DELETE FROM pack_task_serials WHERE task_id = {taskId} AND serial = {serial}", ct) == 1)
        {
            audit.Record(caller.UserId, caller.Role!, "fulfillment.unassign-serial", "pack_task", taskId.ToString(), new { serial });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await Views.PackTaskAsync(db, taskId, ct);
    }

    /// <summary>The new serial takes the old row's <c>line_no</c> and <c>seq</c>.</summary>
    public override async Task<V1.PackTask> ReplaceSerial(ReplaceSerialRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Packers);
        var taskId = Inputs.Id(request.TaskId, "task_id");
        var oldSerial = Inputs.Serial(request.OldSerial, "old_serial");
        var newSerial = Inputs.Serial(request.NewSerial, "new_serial");
        var ct = context.CancellationToken;

        var task = await TaskAsync(taskId, TaskStatuses.Packing, forUpdate: false, ct);
        if (ReplaceDone(await ScannedAsync(taskId, ct), oldSerial, newSerial)) return await Views.PackTaskAsync(db, taskId, ct);
        await inventory.ReplaceAsync(task.RefId, oldSerial, newSerial, context);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await TaskAsync(taskId, TaskStatuses.Packing, forUpdate: true, ct);
        if (!ReplaceDone(await ScannedAsync(taskId, ct), oldSerial, newSerial))
        {
            await db.Database.ExecuteSqlAsync($"UPDATE pack_task_serials SET serial = {newSerial}, scanned_at = {clock.GetUtcNow()} WHERE task_id = {taskId} AND serial = {oldSerial}", ct);
            audit.Record(caller.UserId, caller.Role!, "fulfillment.replace-serial", "pack_task", taskId.ToString(), new { oldSerial, newSerial });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await Views.PackTaskAsync(db, taskId, ct);
    }

    /// <summary>§6.2 step 2 in one transaction: versioned <c>packing → packed</c>, the outbound shipment, and (order tasks) the coverages of the first N serials by <c>seq</c> of each plan's parent line (FR-06).</summary>
    public override async Task<CompletePackingResponse> CompletePacking(CompletePackingRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Packers);
        var taskId = Inputs.Id(request.TaskId, "task_id");
        var ct = context.CancellationToken;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var task = await TaskAsync(taskId, TaskStatuses.Packing, forUpdate: true, ct);
        var lines = await db.PackTaskLines.AsNoTracking().Where(l => l.TaskId == taskId).ToListAsync(ct);
        var scanned = await ScannedAsync(taskId, ct);
        if (lines.Any(l => l is { Kind: LineKinds.Product, Serialized: true } && scanned.Count(s => s.LineNo == l.LineNo) < l.Quantity))
            throw FulfillmentErrors.Error(FulfillmentErrors.SerialsIncomplete, "Every serialized line needs all its serials.");

        var now = clock.GetUtcNow();
        await updates.UpdateVersionedAsync("pack task", db.PackTasks.Where(p => p.Id == taskId && p.Status == TaskStatuses.Packing), request.Version,
            b => b.SetProperty(p => p.Status, TaskStatuses.Packed).SetProperty(p => p.UpdatedAt, now), FulfillmentErrors.NotFoundReason, ct);
        var shipment = new Shipment
        {
            Id = Guid.CreateVersion7(), PackTaskId = taskId, OrderId = task.OrderId, Direction = Directions.Outbound, Service = task.ShippingOption!,
            Status = ShipmentStatuses.LabelPending, NextAttemptAt = now, CreatedAt = now,
        };
        db.Shipments.Add(shipment);
        if (task.Kind == TaskKinds.Reship)
            await MoveCoveragesAsync(task.OrderId, taskId, scanned, ct);
        else
            foreach (var plan in lines.Where(l => l.Kind == LineKinds.WarrantyPlan))
                foreach (var covered in scanned.Where(s => s.LineNo == plan.ParentLineNo).OrderBy(s => s.Seq).Take(plan.Quantity))
                    db.WarrantyCoverages.Add(new WarrantyCoverage
                    {
                        Id = Guid.CreateVersion7(), OrderId = task.OrderId, PlanLineNo = plan.LineNo, DeviceLineNo = covered.LineNo, Serial = covered.Serial,
                        CoverageMonths = plan.CoverageMonths!.Value, Status = CoverageStatuses.PendingDelivery,
                    });
        audit.Record(caller.UserId, caller.Role!, "fulfillment.complete-packing", "pack_task", taskId.ToString(), new { shipmentId = shipment.Id });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CompletePackingResponse { Task = await Views.PackTaskAsync(db, taskId, ct), Shipment = Views.Shipment(shipment) };
    }

    /// <summary>
    /// A reship adds no coverage (§6.5, spec §7): each <c>pending_delivery</c> coverage of the order moves to the reship's serial of the same rank (<c>seq</c> order) on the same device line, as the
    /// serial held it in the earlier task of the order. A coverage cancelled meanwhile is left alone (the guarded update finds no row). Lock order: pack_tasks → … → warranty_coverages.
    /// </summary>
    private async Task MoveCoveragesAsync(Guid orderId, Guid taskId, IReadOnlyList<PackTaskSerial> scanned, CancellationToken ct)
    {
        var coverages = await db.WarrantyCoverages.AsNoTracking().Where(c => c.OrderId == orderId && c.Status == CoverageStatuses.PendingDelivery).ToListAsync(ct);
        if (coverages.Count == 0) return;
        var earlier = await db.PackTaskSerials.AsNoTracking().Where(s => db.PackTasks.Any(p => p.Id == s.TaskId && p.OrderId == orderId && p.Id != taskId)).ToListAsync(ct);
        foreach (var coverage in coverages)
        {
            var held = earlier.Single(s => s.Serial == coverage.Serial);
            var rank = earlier.Count(s => s.TaskId == held.TaskId && s.LineNo == held.LineNo && s.Seq < held.Seq);
            var serial = scanned.Where(s => s.LineNo == held.LineNo).OrderBy(s => s.Seq).ElementAt(rank).Serial;
            await db.Database.ExecuteSqlAsync($"UPDATE warranty_coverages SET serial = {serial} WHERE id = {coverage.Id} AND status = {CoverageStatuses.PendingDelivery}", ct);
        }
    }

    /// <summary>The task, which must be in <paramref name="status"/> (<c>INVALID_STATE</c> otherwise). <paramref name="forUpdate"/> takes its row lock first in the lock order (pack_tasks → shipments → …).</summary>
    private async Task<PackTask> TaskAsync(Guid id, string status, bool forUpdate, CancellationToken ct)
    {
        var query = forUpdate ? db.PackTasks.FromSql($"SELECT * FROM pack_tasks WHERE id = {id} FOR UPDATE") : db.PackTasks.Where(p => p.Id == id);
        var task = (await query.AsNoTracking().ToListAsync(ct)).SingleOrDefault() ?? throw FulfillmentErrors.NotFound("The pack task");
        return task.Status == status ? task : throw FulfillmentErrors.Error(FulfillmentErrors.InvalidState, $"The pack task is {task.Status}, not {status}.");
    }

    private Task<List<PackTaskSerial>> ScannedAsync(Guid taskId, CancellationToken ct) =>
        db.PackTaskSerials.AsNoTracking().Where(s => s.TaskId == taskId).OrderBy(s => s.Seq).ToListAsync(ct);

    private async Task<PackTaskLine> ScannableLineAsync(Guid taskId, int lineNo, CancellationToken ct) =>
        await db.PackTaskLines.AsNoTracking().SingleOrDefaultAsync(l => l.TaskId == taskId && l.LineNo == lineNo && l.Kind == LineKinds.Product && l.Serialized, ct)
        ?? throw FulfillmentErrors.Validation("line_no", "The line is not a serialized product line of this task.");

    /// <summary>True when <paramref name="serial"/> is already on <paramref name="line"/> (a repeat). Another line, or a full line, is <c>VALIDATION_FAILED</c>.</summary>
    private static bool AlreadyScanned(IReadOnlyList<PackTaskSerial> scanned, PackTaskLine line, string serial)
    {
        if (scanned.SingleOrDefault(s => s.Serial == serial) is { } existing)
            return existing.LineNo == line.LineNo ? true : throw FulfillmentErrors.Validation("serial", "The serial is already scanned on another line.");
        if (scanned.Count(s => s.LineNo == line.LineNo) >= line.Quantity) throw FulfillmentErrors.Validation("line_no", "The line already has all its serials.");
        return false;
    }

    /// <summary>True when the replacement is already in place (the new serial is on the task and the old one is gone, or both are the same). A missing old serial is <c>VALIDATION_FAILED</c>.</summary>
    private static bool ReplaceDone(IReadOnlyList<PackTaskSerial> scanned, string oldSerial, string newSerial)
    {
        var old = scanned.Any(s => s.Serial == oldSerial);
        var next = scanned.Any(s => s.Serial == newSerial);
        if (!old) return next ? true : throw FulfillmentErrors.Validation("old_serial", "The serial is not on this task.");
        return next ? oldSerial == newSerial ? true : throw FulfillmentErrors.Validation("new_serial", "The serial is already on this task.") : false;
    }
}
