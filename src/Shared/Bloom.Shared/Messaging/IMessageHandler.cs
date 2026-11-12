namespace Bloom.Shared.Messaging;

/// <summary>
/// A consumer handler. Resolved from a fresh DI scope per delivery. Call <see cref="Envelope.GetPayload{T}"/> before any side
/// effect: an invalid payload then dead-letters at once (<see cref="PermanentFailureException"/>) with nothing to undo.
/// </summary>
public interface IMessageHandler
{
    Task HandleAsync(Envelope envelope, CancellationToken ct);
}
