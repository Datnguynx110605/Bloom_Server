using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Context;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Bloom.Fulfillment.Errors;
using Bloom.Fulfillment.V1;
using Bloom.Shared.Idempotency;
using Bloom.Shared.Messaging;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using ReturnUnit = Bloom.Fulfillment.Data.ReturnUnit;
using Shipment = Bloom.Fulfillment.Data.Shipment;

namespace Bloom.Fulfillment.Services;

/// <summary>05-fulfillment §3 component <c>Support API</c>: <c>Deciders</c> only (plan R7).</summary>
public sealed class SupportService(
    FulfillmentDbContext db, IdempotencyGuard guard, RefundEngine refunds, Audit audit, OutboxWriter outbox, TimeProvider clock, ILogger<SupportService> logger)
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

        await refunds.ApplyAsync(orderId, perLine, ct);
        var shipping = await refunds.ShippingIfCompleteAsync(account, ct);
        foreach (var id in cancelled) await refunds.CancelCoverageAsync(id, ct);

        var total = perLine.Values.Sum(v => v.Cents) + shipping;
        var refundId = refunds.Send(orderId, returnId, SagaTypes.ReturnRefund, "return", total, RefundEngine.Lines(perLine));
        await SendToInventoryAsync(orderId, accepted, ct);
        outbox.PublishEvent(FulfillmentMessages.ReturnDecided, new ReturnDecidedV1(
            orderId, returnId, accepted.Concat(planUnits).GroupBy(u => u.LineNo).OrderBy(g => g.Key).Select(g => new AcceptedLineV1(g.Key, g.Count())).ToList(), total));
        outbox.PublishEvent(FulfillmentMessages.ReturnClosed, new ReturnClosedV1(orderId, returnId, "decided"));

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
}
