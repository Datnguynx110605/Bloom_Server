using System.Text;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Messaging;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Payment.Tests;

public static class Outbox
{
    /// <summary>The payloads of every outbox message of <paramref name="messageType"/>, oldest first.</summary>
    public static async Task<IReadOnlyList<T>> PayloadsAsync<T>(TestPayment t, string messageType) =>
        (await EnvelopesAsync(t, messageType)).Select(e => e.GetPayload<T>()).ToList();

    /// <summary>The envelopes of every outbox message of <paramref name="messageType"/>, oldest first.</summary>
    public static async Task<List<Envelope>> EnvelopesAsync(TestPayment t, string messageType)
    {
        await using var db = t.NewDb();
        var rows = await db.Set<OutboxMessage>().Where(m => m.MessageType == messageType).OrderBy(m => m.Id)
            .Select(m => m.Payload).ToListAsync(TestContext.Current.CancellationToken);
        return rows.Select(p => Envelope.Parse(Encoding.UTF8.GetBytes(p))).ToList();
    }
}
