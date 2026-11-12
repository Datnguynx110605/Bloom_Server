using System.Text.Json;
using Bloom.Payment.Contracts;
using Bloom.Payment.Data;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Payment.Consumers;

/// <summary>
/// <c>payment.refund-payment.v1</c> (04-payment §6.3, spec §7), in <c>ConsumerHost</c>'s transaction (plan R6, R16): records the refund as <c>pending</c> and reserves its amount on the
/// payment with one guarded update (PR-04); the provider executor makes the Stripe call. Every refusal is permanent (DLQ) and logged as an error naming order and refund (the alert).
/// </summary>
public sealed class RefundPaymentConsumer(PaymentDbContext db, TimeProvider clock, ILogger<RefundPaymentConsumer> logger) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Command(PaymentMessages.RefundPayment)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var command = envelope.GetPayload<RefundPaymentV1>();
        if (command.RefundId == Guid.Empty || command.AmountCents <= 0 || string.IsNullOrWhiteSpace(command.Reason) || command.Lines?.Any(l => l.AmountCents < 0) == true)
            throw Refuse(command, "the refund needs an id, a positive amount, a reason and no negative line amount");

        var payment = await db.Payments.AsNoTracking().Where(p => p.OrderId == command.OrderId).Select(p => new { p.Id, p.Status }).SingleOrDefaultAsync(ct)
            ?? throw Refuse(command, "the order has no payment");

        var now = clock.GetUtcNow();
        var lines = command.Lines is null ? null : JsonSerializer.Serialize(command.Lines, JsonSerializerOptions.Web);
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO refunds (id, payment_id, order_id, amount_cents, reason, lines, status, attempts, next_attempt_at, created_at, updated_at)
            VALUES ({command.RefundId}, {payment.Id}, {command.OrderId}, {command.AmountCents}, {command.Reason}, {lines}::jsonb, {RefundStatuses.Pending}, 0, {now}, {now}, {now})
            ON CONFLICT DO NOTHING
            """, ct);
        if (inserted == 0) return; // a redelivery or a repeated command: one refund per id (P-06)

        if (payment.Status != PaymentStatuses.Succeeded) throw Refuse(command, $"the payment is {payment.Status}, not succeeded");
        var reserved = await db.Database.ExecuteSqlAsync($"""
            UPDATE payments SET refunded_cents = refunded_cents + {command.AmountCents}, version = version + 1, updated_at = {now}
            WHERE id = {payment.Id} AND refunded_cents + {command.AmountCents} <= amount_cents
            """, ct);
        if (reserved == 0) throw Refuse(command, "the refund would exceed the amount paid (PR-04)");
    }

    private PermanentFailureException Refuse(RefundPaymentV1 command, string why)
    {
        logger.LogError("Refund command for order {OrderId}, refund {RefundId} is refused: {Why}", command.OrderId, command.RefundId, why);
        return new PermanentFailureException($"Refund {command.RefundId} of order {command.OrderId}: {why}.");
    }
}
