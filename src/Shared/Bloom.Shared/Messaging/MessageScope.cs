namespace Bloom.Shared.Messaging;

/// <summary>The message a handler is processing; set by the consumer host so the outbox writer can set causation and correlation.</summary>
internal static class MessageScope
{
    private static readonly AsyncLocal<Envelope?> current = new();

    public static Envelope? Current
    {
        get => current.Value;
        set => current.Value = value;
    }
}
