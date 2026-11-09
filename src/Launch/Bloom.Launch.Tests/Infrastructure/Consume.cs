using System.Text.Json;
using Bloom.Launch.Data;
using Bloom.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Launch.Tests;

public static class Consume
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Runs a handler the way <c>ConsumerHost</c> does: own scope, one transaction, SaveChanges, commit (plan R18).</summary>
    public static async Task Async<THandler>(TestLaunch t, string messageType, object payload, Guid? messageId = null) where THandler : IMessageHandler
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = t.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LaunchDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var envelope = new Envelope(messageId ?? Guid.NewGuid(), messageType, MessageKinds.Event, "test", DateTime.UtcNow, Guid.NewGuid(),
            JsonSerializer.SerializeToElement(payload, Web));
        await scope.ServiceProvider.GetRequiredService<THandler>().HandleAsync(envelope, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
