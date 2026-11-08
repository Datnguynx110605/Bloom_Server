using Bloom.Notification.Data;

namespace Bloom.Notification.Tests;

/// <summary>Rows for the tests of the API, the hub, the consumers and the purges.</summary>
public static class Seed
{
    /// <summary>Inserts an in-app notification and returns its id.</summary>
    public static async Task<Guid> NotificationAsync(TestNotification t, Guid accountId, DateTimeOffset createdAt, string type = "order.confirmed", DateTimeOffset? readAt = null)
    {
        var id = Guid.CreateVersion7();
        await using var db = t.NewDb();
        db.Notifications.Add(new NotificationRow
        {
            Id = id, AccountId = accountId, Type = type, Title = $"Title {id:N}", Body = $"Body {id:N}", Link = $"/orders/{id:N}",
            SourceMessageId = Guid.NewGuid(), ReadAt = readAt, CreatedAt = createdAt,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }
}
