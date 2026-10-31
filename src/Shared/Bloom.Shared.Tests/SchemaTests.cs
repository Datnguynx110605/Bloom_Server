using Bloom.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bloom.Shared.Tests;

public class SchemaTests(Containers containers)
{
    // table.column -> "data_type|is_nullable|column_default" (LLD 00-reliability §4.1-§4.3)
    private static readonly Dictionary<string, string> Columns = new()
    {
        ["outbox_messages.id"] = "uuid|NO|",
        ["outbox_messages.message_type"] = "text|NO|",
        ["outbox_messages.kind"] = "text|NO|",
        ["outbox_messages.exchange"] = "text|NO|",
        ["outbox_messages.routing_key"] = "text|NO|",
        ["outbox_messages.payload"] = "jsonb|NO|",
        ["outbox_messages.headers"] = "jsonb|NO|",
        ["outbox_messages.status"] = "text|NO|",
        ["outbox_messages.attempts"] = "integer|NO|0",
        ["outbox_messages.last_error"] = "text|YES|",
        ["outbox_messages.created_at"] = "timestamp with time zone|NO|now()",
        ["outbox_messages.sent_at"] = "timestamp with time zone|YES|",
        ["inbox_messages.consumer"] = "text|NO|",
        ["inbox_messages.message_id"] = "uuid|NO|",
        ["inbox_messages.message_type"] = "text|NO|",
        ["inbox_messages.processed_at"] = "timestamp with time zone|NO|now()",
        ["idempotency_keys.user_id"] = "uuid|NO|",
        ["idempotency_keys.key"] = "text|NO|",
        ["idempotency_keys.operation"] = "text|NO|",
        ["idempotency_keys.request_hash"] = "bytea|NO|",
        ["idempotency_keys.status"] = "text|NO|",
        ["idempotency_keys.locked_until"] = "timestamp with time zone|NO|",
        ["idempotency_keys.response_status"] = "integer|YES|",
        ["idempotency_keys.response_body"] = "bytea|YES|",
        ["idempotency_keys.created_at"] = "timestamp with time zone|NO|now()",
        ["idempotency_keys.completed_at"] = "timestamp with time zone|YES|",
    };

    private static readonly Dictionary<string, string> PrimaryKeys = new()
    {
        ["outbox_messages"] = "id",
        ["inbox_messages"] = "consumer,message_id",
        ["idempotency_keys"] = "user_id,key",
    };

    // table -> fragments that must each appear in one index definition
    private static readonly (string Table, string Fragment)[] Indexes =
    [
        ("outbox_messages", "(id) WHERE (status = 'pending'::text)"),
        ("outbox_messages", "(sent_at) WHERE (status = 'sent'::text)"),
        ("inbox_messages", "(processed_at)"),
        ("idempotency_keys", "(created_at)"),
    ];

    // table -> (column, allowed values) check constraints
    private static readonly (string Table, string Column, string[] Values)[] Checks =
    [
        ("outbox_messages", "kind", ["event", "command"]),
        ("outbox_messages", "status", ["pending", "sent"]),
        ("idempotency_keys", "status", ["in_progress", "completed"]),
    ];

    [Fact]
    public async Task Tables_match_section_4()
    {
        using var host = await TestApp.BuildAsync(containers);
        var cs = host.Services.GetRequiredService<TestDbContext>().Database.GetConnectionString();
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        var actualColumns = new Dictionary<string, string>();
        await foreach (var r in Query(conn,
            "select table_name || '.' || column_name, data_type || '|' || is_nullable || '|' || coalesce(column_default, '') " +
            "from information_schema.columns where table_schema = 'public' " +
            "and table_name in ('outbox_messages','inbox_messages','idempotency_keys')"))
            actualColumns[r[0]] = r[1];
        Assert.Equal(Columns.OrderBy(p => p.Key), actualColumns.OrderBy(p => p.Key));

        foreach (var (table, pk) in PrimaryKeys)
        {
            var cols = new List<string>();
            await foreach (var r in Query(conn,
                "select kcu.column_name from information_schema.table_constraints tc " +
                "join information_schema.key_column_usage kcu using (constraint_schema, constraint_name) " +
                $"where tc.constraint_type = 'PRIMARY KEY' and tc.table_name = '{table}' order by kcu.ordinal_position"))
                cols.Add(r[0]);
            Assert.Equal(pk, string.Join(',', cols));
        }

        var indexDefs = new List<(string Table, string Def)>();
        await foreach (var r in Query(conn, "select tablename, indexdef from pg_indexes where schemaname = 'public'"))
            indexDefs.Add((r[0], r[1]));
        foreach (var (table, fragment) in Indexes)
            Assert.Contains(indexDefs, d => d.Table == table && d.Def.Contains(fragment));

        var checkDefs = new List<(string Table, string Def)>();
        await foreach (var r in Query(conn,
            "select conrelid::regclass::text, pg_get_constraintdef(oid) from pg_constraint where contype = 'c' and conrelid <> 0"))
            checkDefs.Add((r[0], r[1]));
        foreach (var (table, column, values) in Checks)
            Assert.Contains(checkDefs, d => d.Table == table && d.Def.Contains(column)
                && values.All(v => d.Def.Contains($"'{v}'")));
        Assert.Equal(Checks.Length, checkDefs.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Order")]
    [InlineData("order service")]
    public void AddBloom_rejects_invalid_service_name(string name) =>
        Assert.Throws<ArgumentException>(() =>
            new ServiceCollection().AddBloom<TestDbContext>(o => o.ServiceName = name));

    private static async IAsyncEnumerable<string[]> Query(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            yield return Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetString(i)).ToArray();
    }
}
