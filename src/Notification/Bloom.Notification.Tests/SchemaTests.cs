using Bloom.Notification.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Notification.Tests;

public class SchemaTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Id() => Guid.CreateVersion7();

    private static NotificationRow NewNotification(Guid sourceMessageId, string type) => new()
    {
        Id = Id(),
        AccountId = Id(),
        Type = type,
        Title = "t",
        Body = "b",
        Link = "/orders/BLM-1",
        SourceMessageId = sourceMessageId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static EmailJobRow NewJob(Guid sourceMessageId, string template, string status = EmailStatuses.Pending) => new()
    {
        Id = Id(),
        ToEmail = "a@bloom.test",
        Template = template,
        Data = "{}",
        Status = status,
        NextAttemptAt = DateTimeOffset.UtcNow,
        SourceMessageId = sourceMessageId,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<string> SqlStateAsync(NotificationDbContext db, object entity)
    {
        db.Add(entity);
        var e = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        return Assert.IsType<Npgsql.PostgresException>(e.InnerException).SqlState;
    }

    [Fact]
    public async Task Migration_applies_and_model_has_no_pending_changes()
    {
        await using var t = await TestNotification.StartAsync(containers);
        await using var db = t.NewDb();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task One_notification_per_event_and_type()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var source = Id();
        await using (var db = t.NewDb())
        {
            db.Add(NewNotification(source, "order.confirmed"));
            db.Add(NewNotification(source, "order.shipped")); // another type is fine
            await db.SaveChangesAsync(Ct);
        }

        await using var again = t.NewDb();
        Assert.Equal("23505", await SqlStateAsync(again, NewNotification(source, "order.confirmed")));
    }

    [Fact]
    public async Task One_email_job_per_event_and_template()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var source = Id();
        await using (var db = t.NewDb())
        {
            db.Add(NewJob(source, "order.confirmed"));
            db.Add(NewJob(source, "order.shipped"));
            await db.SaveChangesAsync(Ct);
        }

        await using var again = t.NewDb();
        Assert.Equal("23505", await SqlStateAsync(again, NewJob(source, "order.confirmed")));
    }

    [Fact]
    public async Task Email_status_rejects_unknown_values()
    {
        await using var t = await TestNotification.StartAsync(containers);
        await using var db = t.NewDb();

        Assert.Equal("23514", await SqlStateAsync(db, NewJob(Id(), "order.confirmed", status: "x")));
    }

    [Fact]
    public async Task Indexes_match_the_design()
    {
        await using var t = await TestNotification.StartAsync(containers);
        await using var db = t.NewDb();
        var defs = await db.Database.SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public'").ToListAsync(Ct);

        (string Name, string Tail)[] expected =
        [
            ("ux_notifications_source_message_id_type", "(source_message_id, type)"),
            ("ix_notifications_account_id_created_at", "(account_id, created_at DESC)"),
            ("ix_notifications_account_id_unread", "(account_id) WHERE (read_at IS NULL)"),
            ("ux_email_jobs_source_message_id_template", "(source_message_id, template)"),
            ("ix_email_jobs_status_next_attempt_at", "(status, next_attempt_at)"),
        ];
        foreach (var (name, tail) in expected)
            Assert.Contains(defs, d => d.Contains($" {name} ON ") && d.EndsWith(tail));
    }

    [Fact]
    public async Task After_commit_actions_run_only_on_commit()
    {
        await using var t = await TestNotification.StartAsync(containers);
        var ran = new List<string>();

        await using (var scope = t.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            db.AfterCommit.Add(() => { ran.Add("committed"); return Task.CompletedTask; });
            Assert.Empty(ran); // not before the commit
            await tx.CommitAsync(Ct);
            Assert.Equal(["committed"], ran);
        }

        await using (var scope = t.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            db.AfterCommit.Add(() => { ran.Add("rolled back"); return Task.CompletedTask; });
            await tx.RollbackAsync(Ct);
            Assert.Empty(db.AfterCommit);
        }

        Assert.Equal(["committed"], ran);
    }
}
