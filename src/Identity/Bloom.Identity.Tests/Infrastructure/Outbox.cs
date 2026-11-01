using Bloom.Shared.Messaging;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Identity.Tests;

public static class Outbox
{
    /// <summary>Envelopes of the given message type in the outbox, oldest first.</summary>
    public static async Task<IReadOnlyList<Envelope>> EnvelopesAsync(TestIdentity t, string messageType)
    {
        await using var db = t.NewDb();
        var rows = await db.Set<OutboxMessage>().Where(m => m.MessageType == messageType).OrderBy(m => m.Id)
            .Select(m => m.Payload).ToListAsync(TestContext.Current.CancellationToken);
        return rows.Select(p => Envelope.Parse(System.Text.Encoding.UTF8.GetBytes(p))).ToList();
    }
}
