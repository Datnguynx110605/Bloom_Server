using System.Text.Json;
using Bloom.Order.Data;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Order.Tests;

public static class Consume
{
    /// <summary>Runs a handler the way <c>ConsumerHost</c> does: own scope, one transaction, SaveChanges, commit (plan R22).</summary>
    public static async Task Async<THandler>(TestOrder t, string messageType, object payload, Guid? messageId = null) where THandler : IMessageHandler
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = t.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var envelope = new Envelope(messageId ?? Guid.NewGuid(), messageType, MessageKinds.Event, "test", DateTime.UtcNow, Guid.NewGuid(),
            JsonSerializer.SerializeToElement(payload, BloomJson.Options));
        await scope.ServiceProvider.GetRequiredService<THandler>().HandleAsync(envelope, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
