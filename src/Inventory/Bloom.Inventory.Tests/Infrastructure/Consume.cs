using System.Text.Json;
using Bloom.Inventory.Data;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Inventory.Tests;

public static class Consume
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Runs a handler the way <c>ConsumerHost</c> does: own scope, one transaction, SaveChanges, commit (plan R22). <paramref name="commit"/> false rolls back instead.</summary>
    public static async Task Async<THandler>(TestInventory t, string messageType, object payload, Guid? messageId = null, bool commit = true) where THandler : IMessageHandler
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = t.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var envelope = new Envelope(messageId ?? Guid.NewGuid(), messageType, MessageKinds.Event, "test", DateTime.UtcNow, Guid.NewGuid(),
            JsonSerializer.SerializeToElement(payload, Web));
        await scope.ServiceProvider.GetRequiredService<THandler>().HandleAsync(envelope, ct);
        await db.SaveChangesAsync(ct);
        if (commit) await tx.CommitAsync(ct);
        else await tx.RollbackAsync(ct);
    }
}
