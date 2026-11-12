using Bloom.Inventory.Domain;
using Npgsql;

namespace Bloom.Inventory.Tests;

/// <summary>
/// Side connections that force an interleaving without sleeping: hold a lock here, start the code under test, poll <c>pg_locks</c> / <c>pg_stat_activity</c>
/// until it waits, then let it go. Session-level locks end when the connection is disposed.
/// </summary>
public static class SideDb
{
    /// <summary>Backends waiting on any lock of this test's database.</summary>
    public const string LockWaiters = "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'";

    /// <summary>The advisory key <see cref="GateInsertsAsync"/> parks inserts on.</summary>
    public const int GateKey = 7777;

    /// <summary>Backends parked by <see cref="GateInsertsAsync"/>.</summary>
    public static readonly string GateWaiters = $"SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND objid = {GateKey}";

    /// <summary>
    /// Makes inserts into <paramref name="table"/> that match <paramref name="condition"/> (a PL/pgSQL expression on <c>NEW</c>) wait for a shared advisory lock,
    /// and returns the connection that holds it exclusively: <c>pg_advisory_unlock(GateKey)</c> on it lets them through.
    /// </summary>
    public static async Task<NpgsqlConnection> GateInsertsAsync(TestInventory t, string table, string condition)
    {
        await using (var owner = await OpenAsync(t))
            await owner.ExecAsync($"""
                CREATE FUNCTION gate_{table}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF {condition} THEN PERFORM pg_advisory_xact_lock_shared({GateKey}); END IF; RETURN NEW; END $$;
                CREATE TRIGGER gate BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION gate_{table}();
                """);
        var gate = await OpenAsync(t);
        await gate.ExecAsync($"SELECT pg_advisory_lock({GateKey})");
        return gate;
    }

    public static async Task<NpgsqlConnection> OpenAsync(TestInventory t)
    {
        var connection = new NpgsqlConnection(t.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    public static async Task ExecAsync(this NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public static async Task<T> ScalarAsync<T>(this NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public static Task WaitForAsync(this NpgsqlConnection probe, string countSql, int expected, string what) =>
        Poll.UntilAsync(async () => await probe.ScalarAsync<long>(countSql) == expected, 20, what);

    /// <summary>Counts the backends waiting for the advisory lock of a one-bigint key (a SKU, see <see cref="StockLocks.Key"/>).</summary>
    public static string WaitersOn(long key) =>
        $"SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND ((classid::int8 << 32) | objid::int8) = {key}";
}
