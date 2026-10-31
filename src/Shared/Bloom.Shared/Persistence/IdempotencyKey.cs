namespace Bloom.Shared.Persistence;

/// <summary>Row of <c>idempotency_keys</c> (LLD 00-reliability §4.3).</summary>
public sealed class IdempotencyKey
{
    public Guid UserId { get; set; }
    public string Key { get; set; } = "";
    public string Operation { get; set; } = "";
    public byte[] RequestHash { get; set; } = [];
    public string Status { get; set; } = "";
    public DateTime LockedUntil { get; set; }
    public int? ResponseStatus { get; set; }
    public byte[]? ResponseBody { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
