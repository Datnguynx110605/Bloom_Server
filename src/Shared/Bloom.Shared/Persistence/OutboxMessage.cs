namespace Bloom.Shared.Persistence;

/// <summary>Row of <c>outbox_messages</c> (LLD 00-reliability §4.1).</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string MessageType { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string RoutingKey { get; set; } = "";
    public string Payload { get; set; } = "";
    public string Headers { get; set; } = "";
    public string Status { get; set; } = "";
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}
