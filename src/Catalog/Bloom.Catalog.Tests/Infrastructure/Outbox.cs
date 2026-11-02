using Bloom.Identity.Contracts;
using Bloom.Shared.DeadLetters;
using Bloom.Shared.Messaging;
using Bloom.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Catalog.Tests;

public static class Outbox
{
    /// <summary>The <c>staff-action-recorded</c> payloads in the outbox, oldest first.</summary>
    public static async Task<IReadOnlyList<StaffActionRecorded>> AuditAsync(TestCatalog t)
    {
        await using var db = t.NewDb();
        var rows = await db.Set<OutboxMessage>().Where(m => m.MessageType == IdentityMessages.StaffActionRecorded).OrderBy(m => m.Id)
            .Select(m => m.Payload).ToListAsync(TestContext.Current.CancellationToken);
        return rows.Select(p => Envelope.Parse(System.Text.Encoding.UTF8.GetBytes(p)).GetPayload<StaffActionRecorded>()).ToList();
    }
}
