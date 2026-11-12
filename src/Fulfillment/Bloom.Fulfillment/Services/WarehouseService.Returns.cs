using Bloom.Fulfillment.Contracts;
using Bloom.Fulfillment.Context;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Bloom.Fulfillment.Errors;
using Bloom.Fulfillment.V1;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using ReturnRequest = Bloom.Fulfillment.Data.ReturnRequest;

namespace Bloom.Fulfillment.Services;

/// <summary>§6.4 (spec §7): the warehouse side of product returns.</summary>
public sealed partial class WarehouseService
{
    private const int MaxNotesLength = 500;

    /// <summary>Oldest first. The status filter must be a return status (R10).</summary>
    public override async Task<ListReturnsResponse> ListReturns(ListReturnsRequest request, ServerCallContext context)
    {
        Caller.From(context).RequireStaff(Caller.ReturnViewers);
        var (skip, take) = Inputs.Paging(request.Page, request.PageSize);
        if (request.HasStatus && !ReturnStatuses.All.Contains(request.Status)) throw FulfillmentErrors.Validation("status", "Not a return status.");

        var returns = db.ReturnRequests.AsNoTracking();
        if (request.HasStatus) returns = returns.Where(r => r.Status == request.Status);
        var page = await returns.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Skip(skip).Take(take).ToListAsync(context.CancellationToken);
        return new ListReturnsResponse { Items = { await Views.ReturnsAsync(db, page, context.CancellationToken) } };
    }

    /// <summary><c>label_issued | in_transit | expired → received</c> (RET-R15: a parcel that arrives after expiry is inspected and decided as usual, D33) in one guarded update (R9); zero rows: the status says why.</summary>
    public override async Task<V1.ReturnRequest> ReceiveReturn(ReceiveReturnRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Packers);
        var returnId = Inputs.Id(request.ReturnId, "return_id");
        var ct = context.CancellationToken;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        var updated = await db.Database.SqlQuery<Guid>($"""
            UPDATE return_requests SET status = {ReturnStatuses.Received}, version = version + 1, updated_at = {now}
            WHERE id = {returnId} AND status IN ({ReturnStatuses.LabelIssued}, {ReturnStatuses.InTransit}, {ReturnStatuses.Expired})
            RETURNING order_id AS "Value"
            """).ToListAsync(ct);
        if (updated.Count == 0) throw await WrongReturnStateAsync(db, returnId, "label_issued, in_transit or expired", ct);

        outbox.PublishEvent(FulfillmentMessages.ReturnReceived, new ReturnReceivedV1(updated[0], returnId));
        audit.Record(caller.UserId, caller.Role!, "fulfillment.receive-return", "return_request", returnId.ToString(), new { orderId = updated[0] });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await Views.ReturnAsync(db, returnId, ct);
    }

    /// <summary>
    /// Validation first (R10), then the guarded <c>received → inspected</c> (R9). Every unit exactly once; serialized lines need a valid serial, the others none.
    /// A serial is stored with <c>wrong_item</c>, whatever the staff member chose, when it was not shipped on the unit's own line of this order or another return of the order already holds it
    /// (<c>received</c> / <c>inspected</c>, or <c>decided</c> with a unit decision other than <c>reject</c>). One serial on two units of the call is <c>VALIDATION_FAILED</c>.
    /// </summary>
    public override async Task<V1.ReturnRequest> RecordInspection(RecordInspectionRequest request, ServerCallContext context)
    {
        var caller = Caller.From(context).RequireStaff(Caller.Packers);
        var returnId = Inputs.Id(request.ReturnId, "return_id");
        var ct = context.CancellationToken;

        var orderId = await db.ReturnRequests.AsNoTracking().Where(r => r.Id == returnId).Select(r => (Guid?)r.OrderId).SingleOrDefaultAsync(ct)
            ?? throw FulfillmentErrors.NotFound("The return");
        var units = await db.ReturnUnits.Where(u => u.ReturnId == returnId).OrderBy(u => u.UnitNo).ToListAsync(ct);
        if (!request.Units.Select(u => u.UnitNo).Order().SequenceEqual(units.Select(u => u.UnitNo)))
            throw FulfillmentErrors.Validation("units", "Give every unit of the return exactly once.");
        var serializedLines = await db.PackTaskLines.AsNoTracking()
            .Where(l => l.Serialized && db.PackTasks.Any(p => p.Id == l.TaskId && p.OrderId == orderId && p.Kind == TaskKinds.Order)).Select(l => l.LineNo).ToListAsync(ct);

        var given = request.Units.ToDictionary(u => u.UnitNo);
        var serials = new Dictionary<int, string?>();
        foreach (var unit in units)
        {
            var u = given[unit.UnitNo];
            if (!InspectionResults.All.Contains(u.Result)) throw FulfillmentErrors.Validation("units", $"Unit {unit.UnitNo}: not an inspection result.");
            if (u.Notes.Length > MaxNotesLength) throw FulfillmentErrors.Validation("units", $"Unit {unit.UnitNo}: notes are at most {MaxNotesLength} characters.");
            if (serializedLines.Contains(unit.LineNo)) serials[unit.UnitNo] = Inputs.Serial(u.ReceivedSerial, "units");
            else if (!string.IsNullOrWhiteSpace(u.ReceivedSerial)) throw FulfillmentErrors.Validation("units", $"Unit {unit.UnitNo}: its line is not serialized.");
            else serials[unit.UnitNo] = null;
        }
        var claimed = serials.Values.OfType<string>().ToList();
        if (claimed.Distinct().Count() != claimed.Count) throw FulfillmentErrors.Validation("units", "A serial can be on one unit only.");
        var shipped = (await db.PackTaskSerials.AsNoTracking().Where(s => claimed.Contains(s.Serial) && db.PackTasks.Any(p => p.Id == s.TaskId && p.OrderId == orderId))
            .Select(s => new { s.Serial, s.LineNo }).ToListAsync(ct)).Select(s => (s.Serial, s.LineNo)).ToHashSet();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = clock.GetUtcNow();
        if (await db.Database.ExecuteSqlAsync($"""
            UPDATE return_requests SET status = {ReturnStatuses.Inspected}, version = version + 1, updated_at = {now}
            WHERE id = {returnId} AND status = {ReturnStatuses.Received}
            """, ct) != 1)
            throw await WrongReturnStateAsync(db, returnId, "received", ct);

        // Lock order: return_requests → order_accounts. Two returns of one order can't both take a serial.
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM order_accounts WHERE order_id = {orderId} FOR UPDATE", ct);
        var held = await db.ReturnUnits.AsNoTracking()
            .Where(u => claimed.Contains(u.ReceivedSerial!) && db.ReturnRequests.Any(r => r.Id == u.ReturnId && r.OrderId == orderId && r.Id != returnId
                && (r.Status == ReturnStatuses.Received || r.Status == ReturnStatuses.Inspected || (r.Status == ReturnStatuses.Decided && u.Decision != Decisions.Reject))))
            .Select(u => u.ReceivedSerial!).ToListAsync(ct);

        foreach (var unit in units)
        {
            var serial = serials[unit.UnitNo];
            unit.ReceivedSerial = serial;
            unit.InspectionResult = serial is not null && (!shipped.Contains((serial, unit.LineNo)) || held.Contains(serial)) ? InspectionResults.WrongItem : given[unit.UnitNo].Result;
            unit.InspectionNotes = given[unit.UnitNo].Notes.Length == 0 ? null : given[unit.UnitNo].Notes;
        }
        audit.Record(caller.UserId, caller.Role!, "fulfillment.record-inspection", "return_request", returnId.ToString(), new { orderId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await Views.ReturnAsync(db, returnId, ct);
    }

    /// <summary>R9: a guarded update affected no row, so the row's presence says <c>NOT_FOUND</c> or <c>INVALID_RETURN_STATE</c>.</summary>
    internal static async Task<RpcException> WrongReturnStateAsync(FulfillmentDbContext db, Guid returnId, string needs, CancellationToken ct)
    {
        var status = await db.ReturnRequests.AsNoTracking().Where(r => r.Id == returnId).Select(r => r.Status).SingleOrDefaultAsync(ct);
        return status is null
            ? FulfillmentErrors.NotFound("The return")
            : FulfillmentErrors.Error(FulfillmentErrors.InvalidReturnState, $"The return is {status}; it must be {needs}.");
    }
}
