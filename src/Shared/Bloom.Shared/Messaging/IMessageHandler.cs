namespace Bloom.Shared.Messaging;

/// <summary>A consumer handler. Resolved from a fresh DI scope per delivery.</summary>
public interface IMessageHandler
{
    Task HandleAsync(Envelope envelope, CancellationToken ct);
}
