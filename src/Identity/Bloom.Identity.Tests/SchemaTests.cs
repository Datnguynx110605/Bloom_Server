using Bloom.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bloom.Identity.Tests;

public class SchemaTests(Containers containers)
{
    private static Account NewAccount(string email, string type = AccountTypes.Customer, string? role = null, string? googleSub = null, string status = AccountStatuses.Active) => new()
    {
        Id = Guid.CreateVersion7(),
        Type = type,
        EmailNormalized = email,
        Role = role,
        GoogleSub = googleSub,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<string?> SqlStateOfAsync(IdentityDbContext db)
    {
        var e = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        return Assert.IsType<PostgresException>(e.InnerException).SqlState;
    }

    [Fact]
    public async Task Migration_applies_and_model_has_no_pending_changes()
    {
        await using var identity = await TestIdentity.StartAsync(containers);
        await using var db = identity.NewDb();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task One_email_one_account_across_types()
    {
        await using var identity = await TestIdentity.StartAsync(containers);
        await using var db = identity.NewDb();
        db.Accounts.AddRange(NewAccount("a@x.com"), NewAccount("a@x.com", AccountTypes.Staff, StaffRoles.Support));

        Assert.Equal("23505", await SqlStateOfAsync(db));
    }

    [Fact]
    public async Task Google_sub_unique_but_nullable()
    {
        await using var identity = await TestIdentity.StartAsync(containers);
        await using (var db = identity.NewDb())
        {
            db.Accounts.AddRange(NewAccount("a@x.com"), NewAccount("b@x.com"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken); // two nulls
        }

        await using var dup = identity.NewDb();
        dup.Accounts.AddRange(NewAccount("c@x.com", googleSub: "g1"), NewAccount("d@x.com", googleSub: "g1"));
        Assert.Equal("23505", await SqlStateOfAsync(dup));
    }

    [Fact]
    public async Task Check_constraints_reject_unknown_values()
    {
        await using var identity = await TestIdentity.StartAsync(containers);
        foreach (var bad in new[]
        {
            NewAccount("a@x.com", "robot"),
            NewAccount("b@x.com", AccountTypes.Staff, "owner"),
            NewAccount("c@x.com", status: "gone"),
        })
        {
            await using var db = identity.NewDb();
            db.Accounts.Add(bad);
            Assert.Equal("23514", await SqlStateOfAsync(db));
        }
    }

    [Fact]
    public async Task Lld_indexes_exist()
    {
        await using var identity = await TestIdentity.StartAsync(containers);
        await using var conn = new NpgsqlConnection(identity.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE schemaname = 'public'", conn);
        var defs = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            while (await reader.ReadAsync(TestContext.Current.CancellationToken)) defs.Add(reader.GetString(0));

        // (table, columns, predicate or null) of LLD 06-identity §4.1, plus addresses(account_id).
        (string Table, string Columns, string? Where)[] expected =
        [
            ("refresh_tokens", "(family_id)", null),
            ("refresh_tokens", "(account_id)", "WHERE (status = 'active'::text)"),
            ("verification_codes", "(target_email, purpose)", "WHERE (consumed_at IS NULL)"),
            ("audit_log", "(occurred_at)", null),
            ("audit_log", "(actor_id, occurred_at)", null),
            ("audit_log", "(target_type, target_id)", null),
            ("addresses", "(account_id)", null),
            ("accounts", "(email_normalized)", null),
            ("accounts", "(google_sub)", "WHERE (google_sub IS NOT NULL)"),
            ("refresh_tokens", "(token_hash)", null),
            ("staff_invitations", "(token_hash)", null),
        ];
        foreach (var (table, columns, where) in expected)
            Assert.Contains(defs, d => d.Contains($" ON public.{table} ") && d.EndsWith(where is null ? columns : $"{columns} {where}"));
    }

    [Fact]
    public async Task Saving_an_account_bumps_version_and_updated_at()
    {
        await using var identity = await TestIdentity.StartAsync(containers);
        var account = NewAccount("a@x.com");
        await using (var db = identity.NewDb())
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        DateTimeOffset before, after;
        await using (var db = identity.NewDb())
        {
            var tracked = await db.Accounts.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, tracked.Version);
            tracked.Name = "Ann";
            before = await identity.DbNowAsync();
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            after = await identity.DbNowAsync();
        }

        await using var read = identity.NewDb();
        var saved = await read.Accounts.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, saved.Version);
        Assert.InRange(saved.UpdatedAt, before, after);
    }
}
