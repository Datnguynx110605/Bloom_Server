using Bloom.Fulfillment.Data;
using Bloom.Payment.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Fulfillment.Consumers;

/// <summary>
/// <c>payment.refund-failed.v1</c> (spec §7 saga): the saga stays open with its step pending, so the "stuck > 30 min" alert sees it (plan R22). Support follows up from the error log.
/// An unknown <c>refund_id</c> is an order's own refund: nothing to do.
/// </summary>
public sealed class RefundFailedConsumer(FulfillmentDbContext db, ILogger<RefundFailedConsumer> logger) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("payment", PaymentMessages.RefundFailed)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var failed = envelope.GetPayload<RefundFailedV1>();
        if (await db.FulfillmentSagas.AsNoTracking().SingleOrDefaultAsync(s => s.RefundId == failed.RefundId, ct) is not { } saga) return;
        logger.LogError("Refund {RefundId} of {SagaType} {RefId} (order {OrderId}) failed: {FailureReason}. The saga stays open",
            failed.RefundId, saga.Type, saga.RefId, failed.OrderId, failed.FailureReason);
    }
}
