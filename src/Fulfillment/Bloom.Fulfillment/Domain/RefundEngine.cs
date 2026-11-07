using Bloom.Fulfillment.Data;
using Bloom.Fulfillment.Errors;
using Bloom.Fulfillment.Observability;
using Bloom.Payment.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Domain;

/// <summary>Integer money math of §6.4.1 (plan R18, owner item 1).</summary>
public static class RefundMath
{
    /// <summary>
    /// The share of the next unit of a line: <c>floor(paid / quantity)</c>, except the unit that makes <c>refundedUnits = quantity</c>, which gets
    /// <c>paid − (quantity − 1) × floor(paid / quantity)</c>. Units decide, not cents, so an earlier partial refund never inflates the last share (RET-R06, FR-02). Tax follows the same rule.
    /// </summary>
    public static (long Cents, long Tax) UnitShare(long paidCents, long taxCents, int quantity, int refundedUnitsBefore) =>
        refundedUnitsBefore + 1 == quantity
            ? (paidCents - (quantity - 1) * (paidCents / quantity), taxCents - (quantity - 1) * (taxCents / quantity))
            : (paidCents / quantity, taxCents / quantity);

    /// <summary><c>round_half_up(partial × unitTax / share)</c> (RET-R14). <paramref name="partial"/> is above 0 and at most <paramref name="share"/>, so <paramref name="share"/> is above 0.</summary>
    public static long PartialTax(long partial, long share, long unitTax) => (2 * partial * unitTax + share) / (2 * share);
}

/// <summary>
/// The refund steps both <c>DecideReturn</c> and a plan-only <c>RequestReturn</c> share (§6.4.1). Every call but <see cref="Send"/> assumes the caller is inside a transaction
/// that took <see cref="LockOrderAsync"/> first (lock order: … → order_accounts → order_line_accounts, ascending <c>line_no</c> → warranty_coverages).
/// </summary>
public sealed class RefundEngine(FulfillmentDbContext db, OutboxWriter outbox, FulfillmentMetrics metrics, TimeProvider clock)
{
    /// <summary><c>SELECT … FOR UPDATE</c> on the order's account: it serializes the refunds of one order.</summary>
    public async Task<OrderAccount> LockOrderAsync(Guid orderId, CancellationToken ct) =>
        (await db.OrderAccounts.FromSql($"SELECT * FROM order_accounts WHERE order_id = {orderId} FOR UPDATE").AsNoTracking().ToListAsync(ct)).SingleOrDefault()
        ?? throw FulfillmentErrors.NotFound("The order");

    /// <summary>The share of the next unit of <paramref name="line"/>, after the units already counted in <paramref name="perLine"/> for this refund.</summary>
    public static (long Cents, long Tax) NextShare(OrderLineAccount line, IReadOnlyDictionary<int, (int Units, long Cents)> perLine) =>
        RefundMath.UnitShare(line.PaidCents, line.TaxCents, line.Quantity, line.RefundedUnits + perLine.GetValueOrDefault(line.LineNo).Units);

    public static void Add(IDictionary<int, (int Units, long Cents)> perLine, int lineNo, long cents)
    {
        perLine.TryGetValue(lineNo, out var current);
        perLine[lineNo] = (current.Units + 1, current.Cents + cents);
    }

    /// <summary>The per-line breakdown of the refund command, by <c>line_no</c>.</summary>
    public static List<RefundLineV1> Lines(IReadOnlyDictionary<int, (int Units, long Cents)> perLine) =>
        perLine.OrderBy(p => p.Key).Select(p => new RefundLineV1(p.Key, p.Value.Cents)).ToList();

    /// <summary>Guarded line updates in ascending <c>line_no</c> (R18). Zero rows cannot happen under the order lock: it would be a bug, not a business error.</summary>
    public async Task ApplyAsync(Guid orderId, IReadOnlyDictionary<int, (int Units, long Cents)> perLine, CancellationToken ct)
    {
        foreach (var (lineNo, (units, cents)) in perLine.OrderBy(p => p.Key))
            if (await db.Database.ExecuteSqlAsync($"""
                UPDATE order_line_accounts SET refunded_units = refunded_units + {units}, refunded_cents = refunded_cents + {cents}
                WHERE order_id = {orderId} AND line_no = {lineNo} AND refunded_units + {units} <= quantity AND refunded_cents + {cents} <= paid_cents
                """, ct) != 1)
                throw new InvalidOperationException($"Order {orderId} line {lineNo} cannot take {units} more refunded unit(s) and {cents} cents.");
    }

    /// <summary>Step 4 (RET-R13): when every product unit is refunded and the shipping is not, flag it and return <c>shipping_cents</c>; else 0.</summary>
    public async Task<long> ShippingIfCompleteAsync(OrderAccount a, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync($"""
            UPDATE order_accounts SET shipping_refunded = true
            WHERE order_id = {a.OrderId} AND NOT shipping_refunded
              AND NOT EXISTS (SELECT 1 FROM order_line_accounts WHERE order_id = {a.OrderId} AND kind = {LineKinds.Product} AND refunded_units < quantity)
            """, ct) == 1 ? a.ShippingCents : 0;

    /// <summary>A plan's coverage ends with its refund (WAR-R05, WAR-R06). Guarded: it must still be open.</summary>
    public async Task CancelCoverageAsync(Guid coverageId, CancellationToken ct)
    {
        if (await db.Database.ExecuteSqlAsync($"""
            UPDATE warranty_coverages SET status = {CoverageStatuses.Cancelled}
            WHERE id = {coverageId} AND status IN ({CoverageStatuses.Active}, {CoverageStatuses.PendingDelivery})
            """, ct) != 1)
            throw new InvalidOperationException($"Coverage {coverageId} is no longer open.");
    }

    /// <summary>
    /// The refund command and its saga row (R19), staged in the caller's transaction. Amount 0 sends nothing and returns null (owner item 4).
    /// <c>refund_amount_cents_total{reason}</c> counts the command.
    /// </summary>
    public Guid? Send(Guid orderId, Guid sagaRef, string sagaType, string reason, long amount, IReadOnlyList<RefundLineV1> lines)
    {
        if (amount == 0) return null;
        var refundId = Guid.CreateVersion7();
        outbox.SendCommand("payment", PaymentMessages.RefundPayment, new RefundPaymentV1(orderId, refundId, amount, reason, lines));
        db.FulfillmentSagas.Add(new FulfillmentSaga { RefId = sagaRef, Type = sagaType, PendingSteps = ["refund"], RefundId = refundId, StartedAt = clock.GetUtcNow() });
        metrics.Refund(reason, amount);
        return refundId;
    }
}
