using System.Text.Json;
using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Domain;
using Bloom.Payment.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Consumers;

/// <summary>
/// <c>payment.refund-failed.v1</c> (05-fulfillment §6.7, D35): the saga stays open with its step pending, so the "stuck > 30 min" alert sees it (plan R22) and Support follows up from the error log.
/// The refundable amount comes back under the <c>order_accounts</c> lock: that refund's per-line units and amounts leave <c>refunded_units</c> and <c>refunded_cents</c> and <c>shipping_refunded</c> resets if it set it (R7).
/// An unknown <c>refund_id</c> is an order's own refund: nothing to do.
/// </summary>
public sealed class RefundFailedConsumer(FulfillmentDbContext db, RefundEngine refunds, ILogger<RefundFailedConsumer> logger) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("payment", PaymentMessages.RefundFailed)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var failed = envelope.GetPayload<RefundFailedV1>();
        if (await db.FulfillmentSagas.AsNoTracking().SingleOrDefaultAsync(s => s.RefundId == failed.RefundId, ct) is not { } known) return;
        logger.LogError("Refund {RefundId} of {SagaType} {RefId} (order {OrderId}) failed: {FailureReason}. The saga stays open. Refund lines {RefundLines}, shipping refunded {RefundedShipping}",
            failed.RefundId, known.Type, known.RefId, failed.OrderId, failed.FailureReason, known.RefundLines, known.RefundedShipping);

        await refunds.LockOrderAsync(failed.OrderId, ct);
        var saga = await db.FulfillmentSagas.AsNoTracking().SingleAsync(s => s.RefundId == failed.RefundId, ct); // read again: a duplicate waited on the lock
        if (saga.CompletedAt is not null) return;
        if (saga.RefundLines is null)
        {
            logger.LogError("Saga {SagaType} {RefId} was created before its refund lines were recorded: nothing is given back for refund {RefundId}", saga.Type, saga.RefId, failed.RefundId);
            return;
        }

        // The guarded update that ends the saga's claim on the amounts: once empty, a second failure of this refund subtracts nothing.
        if (await db.Database.ExecuteSqlAsync($"""
            UPDATE fulfillment_sagas SET refund_lines = '[]'::jsonb, refunded_shipping = false
            WHERE refund_id = {failed.RefundId} AND completed_at IS NULL AND refund_lines IS NOT NULL AND (jsonb_array_length(refund_lines) > 0 OR refunded_shipping)
            """, ct) != 1) return;

        var held = await db.OrderLineAccounts.AsNoTracking().Where(l => l.OrderId == failed.OrderId).ToDictionaryAsync(l => l.LineNo, ct);
        foreach (var line in JsonSerializer.Deserialize<List<RefundedLine>>(saga.RefundLines)!.OrderBy(l => l.LineNo)) // ascending line_no (R18)
        {
            held.TryGetValue(line.LineNo, out var current);
            if ((current?.RefundedCents ?? 0) < line.Cents)
                logger.LogError("Refund {RefundId}: giving back {Cents} cents on order {OrderId} line {LineNo} would take refunded_cents below 0 (it holds {Held}); it is floored at 0",
                    failed.RefundId, line.Cents, failed.OrderId, line.LineNo, current?.RefundedCents ?? 0);
            if ((current?.RefundedUnits ?? 0) < line.Units)
                logger.LogError("Refund {RefundId}: giving back {Units} units on order {OrderId} line {LineNo} would take refunded_units below 0 (it holds {Held}); it is floored at 0",
                    failed.RefundId, line.Units, failed.OrderId, line.LineNo, current?.RefundedUnits ?? 0);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE order_line_accounts SET refunded_units = GREATEST(refunded_units - {line.Units}, 0), refunded_cents = GREATEST(refunded_cents - {line.Cents}, 0)
                WHERE order_id = {failed.OrderId} AND line_no = {line.LineNo}
                """, ct);
        }
        if (saga.RefundedShipping == true) await db.Database.ExecuteSqlAsync($"UPDATE order_accounts SET shipping_refunded = false WHERE order_id = {failed.OrderId}", ct);

        // ponytail: coverages carry no link to the refund that cancelled them, so this names all the order's cancelled ones; add a refund_id on warranty_coverages if Support needs the exact set.
        var cancelled = await db.WarrantyCoverages.AsNoTracking().Where(c => c.OrderId == failed.OrderId && c.Status == CoverageStatuses.Cancelled).Select(c => c.Id).ToListAsync(ct);
        if (cancelled.Count > 0)
            logger.LogError("Refund {RefundId} failed: coverages {Coverages} of order {OrderId} stay cancelled; Support must review them", failed.RefundId, string.Join(", ", cancelled), failed.OrderId);
    }
}
