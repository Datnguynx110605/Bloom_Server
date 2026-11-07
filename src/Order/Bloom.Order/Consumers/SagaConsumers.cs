using Bloom.Inventory.Contracts;
using Bloom.Order.Data;
using Bloom.Order.Domain;
using Bloom.Shared.Messaging;

namespace Bloom.Order.Consumers;

/// <summary><c>inventory.reservation-released.v1</c> (03-order §5.3): completes the <c>inventory_release</c> step of every open saga of the order. An unknown order or a saga without the step: nothing to do.</summary>
public sealed class ReservationReleasedConsumer(Sagas sagas) : IMessageHandler
{
    public static ConsumerBinding[] Bindings { get; } = [ConsumerBinding.Event("inventory", InventoryMessages.ReservationReleased)];

    public Task HandleAsync(Envelope envelope, CancellationToken ct) =>
        sagas.CompleteStepAsync(envelope.GetPayload<ReservationReleasedV1>().OrderId, SagaSteps.InventoryRelease, ct);
}
