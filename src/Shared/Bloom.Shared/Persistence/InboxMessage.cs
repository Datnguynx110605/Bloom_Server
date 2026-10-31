namespace Bloom.Shared.Persistence;

/// <summary>Row of <c>inbox_messages</c> (LLD 00-reliability §4.2).</summary>
public sealed class InboxMessage
{
    public string Consumer { get; set; } = "";
    public Guid MessageId { get; set; }
    public string MessageType { get; set; } = "";
    public DateTime ProcessedAt { get; set; }
}
