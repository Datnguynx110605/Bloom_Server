using Bloom.Payment.Contracts;
using Bloom.Payment.Data;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Payment.Consumers;

/// <summary>
/// <c>payment.cancel-payment.v1</c> (04-payment §6.4), in <c>ConsumerHost</c>'s transaction (plan R6, R16): records the wish on an <c>open</c> payment; the provider executor expires the
/// session. An unknown order, a <c>creating</c> or a final payment is a no-op.
/// </summary>
public sealed class CancelPaymentConsumer(PaymentDbContext db) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Command(PaymentMessages.CancelPayment)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var orderId = envelope.GetPayload<CancelPaymentV1>().OrderId;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE payments SET cancel_requested = true, version = version + 1, updated_at = now()
            WHERE order_id = {orderId} AND status = 'open' AND NOT cancel_requested
            """, ct);
    }
}
