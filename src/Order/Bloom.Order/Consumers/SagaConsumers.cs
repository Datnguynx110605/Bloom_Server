using Bloom.Inventory.Contracts;
using Bloom.Launch.Contracts;
using Bloom.Order.Data;
using Bloom.Order.Domain;
using Bloom.Payment.Contracts;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Consumers;

/// <summary><c>inventory.reservation-released.v1</c> (03-order §5.3): completes the <c>inventory_release</c> step of every open saga of the order. An unknown order or a saga without the step: nothing to do.</summary>
public sealed class ReservationReleasedConsumer(Sagas sagas) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("inventory", InventoryMessages.ReservationReleased)];

    public Task HandleAsync(Envelope envelope, CancellationToken ct) =>
        sagas.CompleteStepAsync(envelope.GetPayload<ReservationReleasedV1>().OrderId, SagaSteps.InventoryRelease, ct);
}

/// <summary><c>launch.hold-released.v1</c> (03-order §5.3): completes the <c>launch_release</c> step of every open saga of the order.</summary>
public sealed class HoldReleasedConsumer(Sagas sagas) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("launch", LaunchMessages.HoldReleased)];

    public Task HandleAsync(Envelope envelope, CancellationToken ct) =>
        sagas.CompleteStepAsync(envelope.GetPayload<HoldReleasedV1>().OrderId, SagaSteps.LaunchRelease, ct);
}

/// <summary>
/// <c>payment.refund-completed.v1</c> (03-order §5.3, spec §7): a refund with <c>Lines</c> (fulfillment's) adds each amount to that line's <c>refunded_cents</c>;
/// <c>ck_order_lines_refund_le_paid</c> keeps it within what was paid (OR-05) and an overflow throws, so nothing of the message is written. Then the <c>refund</c> step of every
/// open saga of the order completes; a refund with no such saga only updates the lines.
/// </summary>
public sealed class RefundCompletedConsumer(OrderDbContext db, Sagas sagas) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("payment", PaymentMessages.RefundCompleted)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var refund = envelope.GetPayload<RefundCompletedV1>();
        if (refund.Lines is { Count: > 0 } lines && await db.Orders.AnyAsync(o => o.Id == refund.OrderId, ct))
            foreach (var line in lines)
            {
                if (line.AmountCents <= 0) throw new PermanentFailureException("payment.refund-completed.v1 has a line amount that is not positive.");
                var amount = line.AmountCents;
                var updated = await db.OrderLines.Where(l => l.OrderId == refund.OrderId && l.LineNo == line.LineNo)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.RefundedCents, l => l.RefundedCents + amount), ct);
                if (updated == 0) throw new PermanentFailureException($"payment.refund-completed.v1 names line {line.LineNo}, which the order does not have.");
            }
        await sagas.CompleteStepAsync(refund.OrderId, SagaSteps.Refund, ct);
    }
}

/// <summary><c>payment.refund-failed.v1</c> (03-order §5.3, spec §4): <c>refund</c> becomes <c>refund_failed</c> in the open saga, which stays open, and one error is logged for the operator.</summary>
public sealed class RefundFailedConsumer(Sagas sagas, ILogger<RefundFailedConsumer> logger) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("payment", PaymentMessages.RefundFailed)];

    public async Task HandleAsync(Envelope envelope, CancellationToken ct)
    {
        var failed = envelope.GetPayload<RefundFailedV1>();
        await sagas.FailRefundAsync(failed.OrderId, ct);
        logger.LogError("Refund {RefundId} of order {OrderId} failed: {FailureReason}. Its saga stays open.", failed.RefundId, failed.OrderId, failed.FailureReason);
    }
}
