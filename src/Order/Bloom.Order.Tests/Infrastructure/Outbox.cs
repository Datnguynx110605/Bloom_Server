using System.Text;
using Bloom.Identity.Contracts;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Messaging;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Tests;

public static class Outbox
{
    /// <summary>The <c>staff-action-recorded</c> payloads in the outbox, oldest first.</summary>
    public static Task<IReadOnlyList<StaffActionRecorded>> AuditAsync(TestOrder t) => PayloadsAsync<StaffActionRecorded>(t, IdentityMessages.StaffActionRecorded);

    /// <summary>The payloads of every outbox message of <paramref name="messageType"/>, oldest first.</summary>
    public static async Task<IReadOnlyList<T>> PayloadsAsync<T>(TestOrder t, string messageType) =>
        (await EnvelopesAsync(t, messageType)).Select(e => e.GetPayload<T>()).ToList();

    private static async Task<List<Envelope>> EnvelopesAsync(TestOrder t, string messageType)
    {
        await using var db = t.NewDb();
        var rows = await db.Set<OutboxMessage>().Where(m => m.MessageType == messageType).OrderBy(m => m.Id)
            .Select(m => m.Payload).ToListAsync(TestContext.Current.CancellationToken);
        return rows.Select(p => Envelope.Parse(Encoding.UTF8.GetBytes(p))).ToList();
    }
}
