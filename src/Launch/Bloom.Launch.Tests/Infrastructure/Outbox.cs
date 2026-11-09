using System.Text;
using Bloom.Identity.Contracts;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Messaging;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Launch.Tests;

public static class Outbox
{
    /// <summary>The payloads of every outbox message of <paramref name="messageType"/>, oldest first.</summary>
    public static async Task<IReadOnlyList<T>> PayloadsAsync<T>(TestLaunch t, string messageType) =>
        (await EnvelopesAsync(t, messageType)).Select(e => e.GetPayload<T>()).ToList();

    public static async Task<StaffActionRecorded> AuditAsync(TestLaunch t, string action) =>
        (await PayloadsAsync<StaffActionRecorded>(t, IdentityMessages.StaffActionRecorded)).Single(a => a.Action == action);

    /// <summary>The envelopes of every outbox message of <paramref name="messageType"/>, oldest first.</summary>
    public static async Task<List<Envelope>> EnvelopesAsync(TestLaunch t, string messageType)
    {
        await using var db = t.NewDb();
        var rows = await db.Set<OutboxMessage>().Where(m => m.MessageType == messageType).OrderBy(m => m.Id)
            .Select(m => m.Payload).ToListAsync(TestContext.Current.CancellationToken);
        return rows.Select(p => Envelope.Parse(Encoding.UTF8.GetBytes(p))).ToList();
    }
}
