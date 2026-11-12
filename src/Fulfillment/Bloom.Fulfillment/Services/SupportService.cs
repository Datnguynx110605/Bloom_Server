using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Context;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Bloom.Fulfillment.Errors;
using Bloom.Fulfillment.V1;
using Bloom.Inventory.Contracts;
using Bloom.Shared.Idempotency;
using Bloom.Shared.Messaging;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using PackTask = Bloom.Fulfillment.Data.PackTask;
using PackTaskLine = Bloom.Fulfillment.Data.PackTaskLine;
using ReturnUnit = Bloom.Fulfillment.Data.ReturnUnit;
using Shipment = Bloom.Fulfillment.Data.Shipment;

namespace Bloom.Fulfillment.Services;

/// <summary>05-fulfillment §3 component <c>Support API</c>: <c>Deciders</c> only (plan R7).</summary>
public sealed class SupportService(
    FulfillmentDbContext db, IdempotencyGuard guard, RefundEngine refunds, Failures failures, InventoryCalls inventory, Audit audit, OutboxWriter outbox, TimeProvider clock,
    IServiceScopeFactory scopes, ILogger<SupportService> logger)
    : FulfillmentSupport.FulfillmentSupportBase
{
    /// <summary>
    /// §6.4.1 in the guard's one transaction (plan R8). Lock order: return_requests (the guarded <c>inspected → decided</c>) → order_accounts → order_line_accounts → warranty_coverages,
    /// so two decisions on one order queue on the order lock and the second sees the first's line counts (Review Focus 1).
    /// </summary>
    public override async Task<DecideReturnResponse> DecideReturn(DecideReturnRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Deciders);
        var returnId = Inputs.Id(request.ReturnId, "return_id");
        var decisions = ParseDecisions(request);
        return await guard.ExecuteAsync(request, context, ct => DecideAsync(caller, returnId, decisions, ct));
    }

    /// <summary>Request-only checks: each unit once, a known decision, <c>partial_cents</c> above 0 for <c>partial</c> and 0 otherwise.</summary>
    private static Dictionary<int, UnitDecision> ParseDecisions(DecideReturnRequest request)
    {
        if (request.Units.Count == 0 || request.Units.Select(u => u.UnitNo).Distinct().Count() != request.Units.Count)
            throw FulfillmentErrors.Validation("units", "Decide each unit once.");
        foreach (var u in request.Units)
            if (!Decisions.All.Contains(u.Decision) || (u.Decision == Decisions.Partial ? u.PartialCents <= 0 : u.PartialCents != 0))
                throw FulfillmentErrors.Validation("units", $"Unit {u.UnitNo}: give full, partial (with partial_cents above 0) or reject (with 0).");
        return request.Units.ToDictionary(u => u.UnitNo);
    }

    private async Task<DecideReturnResponse> DecideAsync(Caller caller, Guid returnId, Dictionary<int, UnitDecision> decisions, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var orderIds = await db.Database.SqlQuery<Guid>($"""
            UPDATE return_requests SET status = {ReturnStatuses.Decided}, version = version + 1, updated_at = {now}
            WHERE id = {returnId} AND status = {ReturnStatuses.Inspected}
            RETURNING order_id AS "Value"
            """).ToListAsync(ct);
        if (orderIds.Count == 0) throw await WarehouseService.WrongReturnStateAsync(db, returnId, "inspected", ct);
        var orderId = orderIds[0];

        // The state check comes first, so a decided return says INVALID_RETURN_STATE (its plan units would fail the coverage check). The units are read under the return's row lock: inspection is final.
        var units = await db.ReturnUnits.Where(u => u.ReturnId == returnId).OrderBy(u => u.UnitNo).ToListAsync(ct);
        if (!decisions.Keys.Order().SequenceEqual(units.Select(u => u.UnitNo))) throw FulfillmentErrors.Validation("units", "Decide every unit of the return exactly once.");
        var account = await refunds.LockOrderAsync(orderId, ct);
        var lines = await db.OrderLineAccounts.AsNoTracking().Where(l => l.OrderId == orderId).ToDictionaryAsync(l => l.LineNo, ct);
        var perLine = new Dictionary<int, (int Units, long Cents)>();

        var accepted = new List<ReturnUnit>();
        foreach (var unit in units)
        {
            var d = decisions[unit.UnitNo];
            unit.Decision = d.Decision;
            if (d.Decision == Decisions.Reject) continue;
            var (share, shareTax) = RefundEngine.NextShare(lines[unit.LineNo], perLine);
            if (d.Decision == Decisions.Partial)
            {
                if (d.PartialCents > share)
                    throw FulfillmentErrors.Error(FulfillmentErrors.PartialExceedsShare, $"Unit {unit.UnitNo}: a partial refund is at most its share, {share} cents.");
                (unit.RefundCents, unit.RefundTaxCents) = (d.PartialCents, RefundMath.PartialTax(d.PartialCents, share, shareTax));
            }
            else
                (unit.RefundCents, unit.RefundTaxCents) = (share, shareTax);
            RefundEngine.Add(perLine, unit.LineNo, unit.RefundCents);
            accepted.Add(unit);
        }

        // WAR-R05: an accepted device brings its plan back in full. A wrong_item serial may be foreign or a duplicate, so it never touches a coverage.
        var serials = accepted.Where(u => u.InspectionResult != InspectionResults.WrongItem && u.ReceivedSerial is not null).Select(u => u.ReceivedSerial!).ToList();
        var coverages = await db.WarrantyCoverages.AsNoTracking()
            .Where(c => c.OrderId == orderId && serials.Contains(c.Serial) && (c.Status == CoverageStatuses.Active || c.Status == CoverageStatuses.PendingDelivery)).ToListAsync(ct);
        var planUnits = new List<ReturnUnit>();
        var cancelled = new List<Guid>();
        var unitNo = units[^1].UnitNo;
        foreach (var unit in accepted)
        {
            if (coverages.FirstOrDefault(c => c.Serial == unit.ReceivedSerial && c.DeviceLineNo == unit.LineNo) is not { } coverage) continue;
            var plan = lines[coverage.PlanLineNo];
            var (share, tax) = RefundEngine.NextShare(plan, perLine);
            RefundEngine.Add(perLine, plan.LineNo, share);
            planUnits.Add(new ReturnUnit { ReturnId = returnId, UnitNo = ++unitNo, LineNo = plan.LineNo, Decision = Decisions.Full, RefundCents = share, RefundTaxCents = tax });
            cancelled.Add(coverage.Id);
        }
        db.ReturnUnits.AddRange(planUnits);

        var (refundId, total) = await refunds.SettleReturnAsync(account, returnId, perLine, cancelled, ct);
        await SendToInventoryAsync(orderId, accepted, ct);

        if (units.Any(u => u.Decision == Decisions.Reject)) // RET-R08: Bloom ships the rejected units back at its cost
            db.Shipments.Add(new Shipment
            {
                Id = Guid.CreateVersion7(), ReturnRequestId = returnId, OrderId = orderId, Direction = Directions.ShipBack, Service = ShippingOptions.Standard,
                Status = ShipmentStatuses.LabelPending, NextAttemptAt = now, CreatedAt = now,
            });
        audit.Record(caller.UserId, caller.Role!, "fulfillment.decide-return", "return_request", returnId.ToString(), new { orderId, refundCents = total });

        var response = new DecideReturnResponse { ReturnId = returnId.ToString(), RefundId = refundId?.ToString() ?? "", AmountCents = total };
        response.Lines.AddRange(RefundEngine.Lines(perLine).Select(l => new RefundLine { LineNo = l.LineNo, AmountCents = l.AmountCents }));
        return response;
    }

    /// <summary>
    /// <c>inventory.receive-customer-return.v1</c> for the accepted product units: serialized ones one by one, the others grouped per line. An accepted serialized unit inspected as
    /// <c>wrong_item</c> is left out with a warning: the item isn't Bloom stock and inventory would dead-letter the serial (owner item 3). Nothing accepted, nothing sent.
    /// </summary>
    private async Task SendToInventoryAsync(Guid orderId, List<ReturnUnit> accepted, CancellationToken ct)
    {
        if (accepted.Count == 0) return;
        var lines = await db.PackTaskLines.AsNoTracking()
            .Where(l => l.Kind == LineKinds.Product && db.PackTasks.Any(p => p.Id == l.TaskId && p.OrderId == orderId && p.Kind == TaskKinds.Order)).ToDictionaryAsync(l => l.LineNo, ct);
        var back = new List<ReturnedUnitV1>();
        foreach (var unit in accepted.Where(u => lines[u.LineNo].Serialized))
        {
            if (unit.InspectionResult == InspectionResults.WrongItem)
                logger.LogWarning("Return unit {UnitNo} of order {OrderId} came back as wrong_item with serial {Serial}; it is refunded but not sent to inventory", unit.UnitNo, orderId, unit.ReceivedSerial);
            else
                back.Add(new ReturnedUnitV1(lines[unit.LineNo].SkuId, unit.ReceivedSerial, 1));
        }
        back.AddRange(accepted.Where(u => !lines[u.LineNo].Serialized).GroupBy(u => u.LineNo).Select(g => new ReturnedUnitV1(lines[g.Key].SkuId, null, g.Count())));
        if (back.Count > 0) outbox.SendCommand("inventory", FulfillmentMessages.ReceiveCustomerReturn, new ReceiveCustomerReturnV1(orderId, back));
    }

    /// <summary>Oldest first by <c>(created_at, shipment_id)</c>. The status filter must be a failure status (R10).</summary>
    public override async Task<ListDeliveryFailuresResponse> ListDeliveryFailures(ListDeliveryFailuresRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.Deciders);
        var (skip, take) = Inputs.Paging(request.Page, request.PageSize);
        if (request.HasStatus && !FailureStatuses.All.Contains(request.Status)) throw FulfillmentErrors.Validation("status", "Not a delivery failure status.");

        var rows = db.DeliveryFailures.AsNoTracking();
        if (request.HasStatus) rows = rows.Where(f => f.Status == request.Status);
        var page = await rows.OrderBy(f => f.CreatedAt).ThenBy(f => f.ShipmentId).Skip(skip).Take(take).ToListAsync(context.CancellationToken);
        return new ListDeliveryFailuresResponse { Items = { await Views.FailuresAsync(db, page, context.CancellationToken) } };
    }

    /// <summary>
    /// <c>possibly_lost</c> → <c>dismissed</c> and the shipment back to its previous status (spec §4). The status is read under the shipment's lock (shipments → delivery_failures),
    /// because <see cref="Failures.DismissAsync"/> would otherwise throw a plain exception for a failure that is not open.
    /// </summary>
    public override async Task<V1.DeliveryFailure> DismissPossiblyLost(DismissPossiblyLostRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Deciders);
        var shipmentId = Inputs.Id(request.ShipmentId, "shipment_id");
        var ct = context.CancellationToken;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var (shipment, failure) = await failures.LockAsync(shipmentId, ct);
        if (failure is not { Status: FailureStatuses.PossiblyLost })
            throw FulfillmentErrors.Error(FulfillmentErrors.InvalidState, "The shipment is not possibly lost.");
        await failures.DismissAsync(shipmentId, ct);
        audit.Record(caller.UserId, caller.Role!, "fulfillment.dismiss-possibly-lost", "shipment", shipmentId.ToString(), new { orderId = shipment.OrderId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await Views.FailureAsync(db, shipmentId, ct);
    }

    /// <summary>
    /// The form comes from <c>resolution</c> (plan R8): <c>refund</c> in the guard's one transaction, <c>reship</c> in its provider form, where inventory's
    /// <c>CommitForReship</c> (with the deterministic <c>reship_id</c>, R12) sits between the two transactions.
    /// </summary>
    public override async Task<V1.DeliveryFailure> ConfirmLost(ConfirmLostRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Deciders);
        var shipmentId = Inputs.Id(request.ShipmentId, "shipment_id");
        return request.Resolution switch
        {
            "refund" => await guard.ExecuteAsync(request, context, ct => ConfirmLostRefundAsync(caller, shipmentId, ct)),
            "reship" => await guard.ExecuteWithProviderAsync(request, context, (_, ct) => CommitReshipAsync(shipmentId, context, ct), (reshipId, ct) => ReshipAsync(caller, shipmentId, reshipId, ct)),
            _ => throw FulfillmentErrors.Validation("resolution", "The resolution is refund or reship."),
        };
    }

    private async Task<V1.DeliveryFailure> ConfirmLostRefundAsync(Caller caller, Guid shipmentId, CancellationToken ct)
    {
        var (shipment, failure) = await failures.LockAsync(shipmentId, ct);
        RequireLost(failure);
        await MarkLostAsync(shipment, ct);
        await refunds.ResolveFailureAsync(shipment.OrderId, shipmentId, FailureKinds.Lost, caller.UserId, null, ct);
        audit.Record(caller.UserId, caller.Role!, "fulfillment.confirm-lost", "shipment", shipmentId.ToString(), new { orderId = shipment.OrderId, resolution = "refund" });
        return await Views.FailureAsync(db, shipmentId, ct);
    }

    /// <summary>The guard's provider step: no locks. It only rejects what is plainly not a lost shipment; <see cref="ReshipAsync"/> decides under lock, because the state can change during this call.</summary>
    private async Task<Guid> CommitReshipAsync(Guid shipmentId, ServerCallContext context, CancellationToken ct)
    {
        var shipment = await db.Shipments.AsNoTracking().SingleOrDefaultAsync(s => s.Id == shipmentId, ct) ?? throw FulfillmentErrors.NotFound("The shipment");
        RequireLost(await db.DeliveryFailures.AsNoTracking().SingleOrDefaultAsync(f => f.ShipmentId == shipmentId, ct));
        var lines = await db.PackTaskLines.AsNoTracking().Where(l => l.TaskId == shipment.PackTaskId && l.Kind == LineKinds.Product).ToListAsync(ct);
        var reshipId = Ids.ReshipId(shipmentId);
        await inventory.CommitForReshipAsync(reshipId, shipment.OrderId, lines.GroupBy(l => l.SkuId).Select(g => (g.Key, g.Sum(l => l.Quantity))), context);
        return reshipId;
    }

    /// <summary>
    /// The guard's apply step: failure → <c>resolved_reship</c> (guarded, so a failure that was dismissed or resolved while inventory was working is <c>INVALID_STATE</c>), shipment → <c>lost</c>,
    /// and the reship's pack task with the same lines and address. No refund, no <c>delivery-failed</c> (§6.5).
    /// D27: a failure no longer open sends <c>inventory.release-reservation.v1 {orderId, reshipId}</c> before <c>INVALID_STATE</c>, unless it is <c>resolved_reship</c> with this same <c>reshipId</c>
    /// (a concurrent call applied it; the id is deterministic per shipment, so releasing it would free the applied reship's stock). The guard rolls a rejected apply back to its savepoint, so the
    /// release is committed on its own scope first, with no cancellation: a client cancel must not drop it. A crash after that commit makes the retry fail <see cref="RequireLost"/> in
    /// <see cref="CommitReshipAsync"/> before any inventory call, so no second release; a crash before it leaves the reservation held (the design's own window).
    /// </summary>
    private async Task<V1.DeliveryFailure> ReshipAsync(Caller caller, Guid shipmentId, Guid reshipId, CancellationToken ct)
    {
        var (shipment, _) = await failures.LockAsync(shipmentId, ct);
        var now = clock.GetUtcNow();
        if (await db.Database.ExecuteSqlAsync($"""
            UPDATE delivery_failures
            SET status = {FailureStatuses.ResolvedReship}, reship_id = {reshipId}, decided_by = {caller.UserId}, updated_at = {now}
            WHERE shipment_id = {shipmentId} AND kind = {FailureKinds.Lost} AND status IN ({FailureStatuses.PossiblyLost}, {FailureStatuses.Confirmed})
            """, ct) != 1)
        {
            var current = await db.DeliveryFailures.AsNoTracking().SingleAsync(f => f.ShipmentId == shipmentId, ct); // under the failure's lock
            if (current is not { Status: FailureStatuses.ResolvedReship } || current.ReshipId != reshipId)
                await using (var scope = scopes.CreateAsyncScope())
                {
                    scope.ServiceProvider.GetRequiredService<OutboxWriter>().SendCommand("inventory", InventoryMessages.ReleaseReservation, new ReleaseReservationV1(shipment.OrderId, reshipId));
                    await scope.ServiceProvider.GetRequiredService<FulfillmentDbContext>().SaveChangesAsync(CancellationToken.None);
                }
            throw FulfillmentErrors.Error(FulfillmentErrors.InvalidState, "The failure is no longer open.");
        }
        var task = await MarkLostAsync(shipment, ct);

        var reship = new PackTask
        {
            Id = Guid.CreateVersion7(), RefId = reshipId, OrderId = shipment.OrderId, Kind = TaskKinds.Reship, Status = TaskStatuses.Pending,
            ShipTo = task.ShipTo, ShippingOption = task.ShippingOption, CreatedAt = now, UpdatedAt = now,
        };
        db.PackTasks.Add(reship);
        db.PackTaskLines.AddRange((await db.PackTaskLines.AsNoTracking().Where(l => l.TaskId == task.Id).ToListAsync(ct)).Select(l => new PackTaskLine
        {
            TaskId = reship.Id, LineNo = l.LineNo, Kind = l.Kind, SkuId = l.SkuId, ProductId = l.ProductId, Serialized = l.Serialized, Quantity = l.Quantity,
            ParentLineNo = l.ParentLineNo, CoverageMonths = l.CoverageMonths,
        }));
        audit.Record(caller.UserId, caller.Role!, "fulfillment.confirm-lost", "shipment", shipmentId.ToString(), new { orderId = shipment.OrderId, resolution = "reship", reshipId });
        return await Views.FailureAsync(db, shipmentId, ct);
    }

    private static void RequireLost(Data.DeliveryFailure? failure)
    {
        if (failure is not { Kind: FailureKinds.Lost, Status: FailureStatuses.PossiblyLost or FailureStatuses.Confirmed })
            throw FulfillmentErrors.Error(FulfillmentErrors.InvalidState, "The shipment has no lost-shipment failure to confirm.");
    }

    /// <summary>Guarded shipment <c>possibly_lost → lost</c> and <c>inventory.mark-lost-in-transit.v1</c> for the task's ref. Returns the shipment's task.</summary>
    private async Task<PackTask> MarkLostAsync(Shipment shipment, CancellationToken ct)
    {
        if (await db.Database.ExecuteSqlAsync($"""
            UPDATE shipments SET status = {ShipmentStatuses.Lost}, version = version + 1
            WHERE id = {shipment.Id} AND status = {ShipmentStatuses.PossiblyLost}
            """, ct) != 1)
            throw FulfillmentErrors.Error(FulfillmentErrors.InvalidState, "The shipment is not possibly lost.");
        var task = await db.PackTasks.AsNoTracking().SingleAsync(p => p.Id == shipment.PackTaskId, ct);
        outbox.SendCommand("inventory", FulfillmentMessages.MarkLostInTransit, new MarkLostInTransitV1(task.RefId));
        return task;
    }

    /// <summary>The coverage of any serial (spec §7).</summary>
    public override async Task<Coverage> GetCoverage(GetCoverageRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.Deciders);
        return await Views.CoverageAsync(db, clock, Inputs.Serial(request.Serial, "serial"), null, context.CancellationToken);
    }
}
