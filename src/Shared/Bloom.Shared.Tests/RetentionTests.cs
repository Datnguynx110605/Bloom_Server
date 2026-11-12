using Bloom.Shared.Persistence;
using Bloom.Shared.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bloom.Shared.Tests;

public class RetentionTests(Containers containers)
{
    private static RetentionPurger Purger(IHost host) =>
        host.Services.GetServices<IHostedService>().OfType<RetentionPurger>().Single();

    [Fact]
    public async Task T19_purge_removes_only_rows_past_retention()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers, b => b.AddWorker());
        var (oldSent, newSent, oldPending) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO outbox_messages (id, message_type, kind, exchange, routing_key, payload, headers, status, created_at, sent_at) VALUES
                ({oldSent}, 't.x.v1', 'event', 'e', 'k', '[]', '[]', 'sent', now() - interval '9 days', now() - interval '8 days'),
                ({newSent}, 't.x.v1', 'event', 'e', 'k', '[]', '[]', 'sent', now() - interval '7 days', now() - interval '6 days'),
                ({oldPending}, 't.x.v1', 'event', 'e', 'k', '[]', '[]', 'pending', now() - interval '30 days', NULL)
                """, ct);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO inbox_messages (consumer, message_id, message_type, processed_at) VALUES
                ('c', {Guid.NewGuid()}, 't.x.v1', now() - interval '15 days'),
                ('c', {Guid.NewGuid()}, 't.x.v1', now() - interval '13 days')
                """, ct);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO idempotency_keys (user_id, key, operation, request_hash, status, locked_until, created_at) VALUES
                ({Guid.NewGuid()}, 'old', 'op', '\x00', 'completed', now(), now() - interval '25 hours'),
                ({Guid.NewGuid()}, 'new', 'op', '\x00', 'completed', now(), now() - interval '23 hours')
                """, ct);
        }

        await Purger(host).PurgeOnceAsync(ct);

        using var check = host.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<TestDbContext>();
        Assert.Equal(new[] { newSent, oldPending }.Order(), (await after.Set<OutboxMessage>().Select(r => r.Id).ToListAsync(ct)).Order());
        Assert.Equal(1, await after.Set<InboxMessage>().CountAsync(ct));
        Assert.Equal(["new"], await after.Set<IdempotencyKey>().Select(k => k.Key).ToListAsync(ct));
    }

    [Fact]
    public async Task Purge_deletes_more_than_one_batch()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = await TestApp.BuildAsync(containers, b => b.AddWorker());
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO inbox_messages (consumer, message_id, message_type, processed_at)
            SELECT 'c', gen_random_uuid(), 't.x.v1', now() - interval '20 days' FROM generate_series(1, 2500)
            """, ct);

        await Purger(host).PurgeOnceAsync(ct);

        Assert.Equal(0, await db.Set<InboxMessage>().CountAsync(ct));
    }
}
