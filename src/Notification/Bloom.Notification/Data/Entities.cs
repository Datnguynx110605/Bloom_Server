namespace Bloom.Notification.Data;

// Column types, constraints and indexes are in NotificationDbContext; LLD 09-notification §4.1.

public static class EmailStatuses
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static readonly string[] All = [Pending, Sent, Failed, Cancelled];
}

public class ContactRow
{
    public Guid AccountId { get; set; }
    public required string Email { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class OrderRefRow
{
    public Guid OrderId { get; set; }
    public Guid AccountId { get; set; }
    public required string OrderNumber { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An in-app notification; <c>ReadAt</c> null = unread.</summary>
public class NotificationRow
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public required string Type { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public required string Link { get; set; }
    /// <summary>The event that caused it.</summary>
    public Guid SourceMessageId { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class EmailJobRow
{
    public Guid Id { get; set; }
    /// <summary>Null for sign-up codes and staff invitations.</summary>
    public Guid? AccountId { get; set; }
    public required string ToEmail { get; set; }
    public required string Template { get; set; }
    /// <summary>jsonb object of string values (plan N16); codes stay encrypted until sent.</summary>
    public required string Data { get; set; }
    public required string Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? LastError { get; set; }
    public Guid SourceMessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}
