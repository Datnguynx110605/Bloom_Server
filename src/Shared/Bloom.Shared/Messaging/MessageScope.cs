namespace Bloom.Shared.Messaging;

/// <summary>The message a handler is processing; set by the consumer host so the outbox writer can set causation and correlation.</summary>
internal static class MessageScope
{
    internal static readonly AsyncLocal<Envelope?> Current = new();
}
