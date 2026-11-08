using System.Text.Json;
using Bloom.Notification.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Notification.Tests;

/// <summary>Rows for the tests of the API, the hub, the consumers and the purges.</summary>
public static class Seed
{
    /// <summary>An unsaved in-app notification; every argument is optional.</summary>
    public static NotificationRow Notification(Guid? accountId = null, DateTimeOffset? createdAt = null, string type = "order.confirmed", DateTimeOffset? readAt = null, Guid? sourceMessageId = null)
    {
        var id = Guid.CreateVersion7();
        return new()
        {
            Id = id, AccountId = accountId ?? Guid.CreateVersion7(), Type = type, Title = $"Title {id:N}", Body = $"Body {id:N}", Link = $"/orders/{id:N}",
            SourceMessageId = sourceMessageId ?? Guid.NewGuid(), ReadAt = readAt, CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };
    }

    /// <summary>An unsaved email job (pending, due at <paramref name="createdAt"/>, an order-confirmed payload) with every argument optional.</summary>
    public static EmailJobRow Job(string template = "order.confirmed", string status = EmailStatuses.Pending, DateTimeOffset? createdAt = null, DateTimeOffset? next = null,
        string? to = null, Dictionary<string, string>? data = null, Guid? accountId = null, Guid? sourceMessageId = null)
    {
        var id = Guid.CreateVersion7();
        var created = createdAt ?? DateTimeOffset.UtcNow;
        return new()
        {
            Id = id, AccountId = accountId, ToEmail = to ?? $"{id:N}@customer.test", Template = template, Status = status, NextAttemptAt = next ?? created,
            SourceMessageId = sourceMessageId ?? Guid.NewGuid(), CreatedAt = created,
            Data = JsonSerializer.Serialize(data ?? new Dictionary<string, string> { ["name"] = "Ada", ["orderNumber"] = "BL-1001" }),
        };
    }

    /// <summary>Inserts an in-app notification and returns its id.</summary>
    public static async Task<Guid> NotificationAsync(TestNotification t, Guid accountId, DateTimeOffset createdAt, string type = "order.confirmed", DateTimeOffset? readAt = null)
    {
        var row = Notification(accountId, createdAt, type, readAt);
        await using var db = t.NewDb();
        db.Notifications.Add(row);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return row.Id;
    }

    /// <summary>Inserts an email job (created now by the test clock) and returns its id.</summary>
    public static async Task<Guid> JobAsync(TestNotification t, string template = "order.confirmed", Dictionary<string, string>? data = null, string status = EmailStatuses.Pending,
        DateTimeOffset? next = null, string? to = null, DateTimeOffset? createdAt = null)
    {
        var row = Job(template, status, createdAt ?? t.Clock.GetUtcNow(), next, to, data);
        await using var db = t.NewDb();
        db.EmailJobs.Add(row);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return row.Id;
    }

    /// <summary>The account's in-app notifications.</summary>
    public static async Task<List<NotificationRow>> RowsAsync(TestNotification t, Guid account)
    {
        await using var db = t.NewDb();
        return await db.Notifications.AsNoTracking().Where(n => n.AccountId == account).ToListAsync(TestContext.Current.CancellationToken);
    }

    public static async Task ContactAsync(TestNotification t, Guid accountId, string email, string name)
    {
        await using var db = t.NewDb();
        db.Contacts.Add(new ContactRow { AccountId = accountId, Email = email, Name = name, UpdatedAt = t.Clock.GetUtcNow() });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public static async Task OrderRefAsync(TestNotification t, Guid orderId, Guid accountId, string orderNumber)
    {
        await using var db = t.NewDb();
        db.OrderRefs.Add(new OrderRefRow { OrderId = orderId, AccountId = accountId, OrderNumber = orderNumber, UpdatedAt = t.Clock.GetUtcNow() });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
