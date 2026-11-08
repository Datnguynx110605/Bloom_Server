using System.Text.Json;
using Bloom.Notification.Data;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Notification.Tests;

public static class Consume
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Runs the event consumer the way <c>ConsumerHost</c> does: own scope, one transaction, SaveChanges, then commit, or a rollback when <paramref name="rollback"/>.
    /// The inbox is bypassed, so the same <paramref name="messageId"/> can be consumed twice.
    /// </summary>
    public static async Task Async(TestNotification t, string messageType, object payload, Guid? messageId = null, bool rollback = false, DateTime? occurredAt = null)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = t.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var envelope = new Envelope(messageId ?? Guid.NewGuid(), messageType, MessageKinds.Event, "test", occurredAt ?? DateTime.UtcNow, Guid.NewGuid(),
            JsonSerializer.SerializeToElement(payload, Web));
        await scope.ServiceProvider.GetRequiredService<Consumers.EventConsumer>().HandleAsync(envelope, ct);
        await db.SaveChangesAsync(ct);
        if (rollback) await tx.RollbackAsync(ct);
        else await tx.CommitAsync(ct);
    }
}
