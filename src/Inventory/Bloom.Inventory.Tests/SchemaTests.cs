using Bloom.Inventory.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Bloom.Inventory.Tests;

public class SchemaTests(Containers containers)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static StockItem NewStockItem(int onHand = 5) => new() { SkuId = Guid.CreateVersion7(), ProductId = Guid.CreateVersion7(), OnHand = onHand, UpdatedAt = DateTimeOffset.UtcNow };

    private static SerialUnit NewSerial(Guid skuId, string serial, string? imei) => new()
    {
        Serial = serial,
        Imei = imei,
        SkuId = skuId,
        Status = SerialStatuses.InStock,
        ReceiptId = Guid.CreateVersion7(),
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<T> InsertAsync<T>(InventoryDbContext db, T entity) where T : notnull
    {
        db.Add(entity);
        await db.SaveChangesAsync(Ct);
        return entity;
    }

    [Fact]
    public async Task Migration_applies_and_model_has_no_pending_changes()
    {
        await using var t = await TestInventory.StartAsync(containers);
        await using var db = t.NewDb();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task Reserved_above_on_hand_is_rejected()
    {
        await using var t = await TestInventory.StartAsync(containers);
        await using var db = t.NewDb();
        var item = await InsertAsync(db, NewStockItem());

        var e = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE stock_items SET reserved = on_hand + 1 WHERE sku_id = {item.SkuId}", Ct));
        Assert.Equal("23514", e.SqlState);
        Assert.True(Db.IsCheckViolation(e, "ck_stock_items_reserved_le_on_hand"));
        Assert.False(Db.IsUniqueViolation(e, "ck_stock_items_reserved_le_on_hand"));
    }

    [Theory, InlineData("on_hand"), InlineData("reserved"), InlineData("committed"), InlineData("returned"), InlineData("damaged"), InlineData("lost")]
    public async Task Negative_counters_are_rejected(string column)
    {
        await using var t = await TestInventory.StartAsync(containers);
        await using var db = t.NewDb();
        var item = await InsertAsync(db, NewStockItem());

        await using var conn = new NpgsqlConnection(t.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand($"UPDATE stock_items SET {column} = -1 WHERE sku_id = @sku", conn);
        cmd.Parameters.AddWithValue("sku", item.SkuId);
        var e = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(Ct));
        Assert.Equal("23514", e.SqlState);
        Assert.True(column == "on_hand" || Db.IsCheckViolation(e, $"ck_stock_items_{column}_non_negative"));
    }

    [Fact]
    public async Task Imei_is_unique_when_present()
    {
        await using var t = await TestInventory.StartAsync(containers);
        await using var db = t.NewDb();
        var item = await InsertAsync(db, NewStockItem());
        db.SerialUnits.AddRange(NewSerial(item.SkuId, "A1", null), NewSerial(item.SkuId, "A2", null), NewSerial(item.SkuId, "A3", "490154203237518"));
        await db.SaveChangesAsync(Ct);

        await using var dup = t.NewDb();
        dup.SerialUnits.Add(NewSerial(item.SkuId, "A4", "490154203237518"));
        var e = await Assert.ThrowsAsync<DbUpdateException>(() => dup.SaveChangesAsync(Ct));
        Assert.True(Db.IsUniqueViolation(e, "ux_serial_units_imei"));
    }

    [Fact]
    public async Task Lld_indexes_exist()
    {
        await using var t = await TestInventory.StartAsync(containers);
        await using var db = t.NewDb();
        var defs = await db.Database.SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public'").ToListAsync(Ct);

        // (table, definition fragments) of LLD 02-inventory §4.
        (string Table, string[] Parts)[] expected =
        [
            ("stock_allocations", ["(sku_id, paid_at, order_id)", "WHERE", "'backordered'"]),
            ("stock_allocations", ["(created_at)", "WHERE", "'reserved'"]),
            ("stock_allocations", ["(order_id)"]),
            ("serial_units", ["(sku_id, status)"]),
            ("serial_units", ["(ref_id)", "WHERE (ref_id IS NOT NULL)"]),
            ("stock_ledger", ["(sku_id, created_at, id)"]),
        ];
        foreach (var (table, parts) in expected)
            Assert.Contains(defs, d => d.Contains($" ON public.{table} ") && parts.All(d.Contains));
    }

    [Fact]
    public async Task I18_ledger_update_and_delete_are_refused_for_the_app_role()
    {
        await using var t = await TestInventory.StartAsync(containers);
        var skuId = Guid.CreateVersion7();
        await using var conn = new NpgsqlConnection(Containers.AsAppRole(t.ConnectionString));
        await conn.OpenAsync(Ct);

        async Task<string?> RunAsync(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("sku", skuId);
            try { await cmd.ExecuteNonQueryAsync(Ct); return null; }
            catch (PostgresException e) { return e.SqlState; }
        }

        Assert.Null(await RunAsync(
            "INSERT INTO stock_ledger (id, sku_id, entry_type, d_on_hand, d_reserved, d_committed, d_returned, d_damaged, d_lost, ref_type, ref_id, actor, created_at) " +
            "VALUES (gen_random_uuid(), @sku, 'received', 1, 0, 0, 0, 0, 0, 'receipt', gen_random_uuid(), 'inventory', now())"));
        Assert.Null(await RunAsync("SELECT count(*) FROM stock_ledger"));
        Assert.Equal("42501", await RunAsync("UPDATE stock_ledger SET actor = 'x'"));
        Assert.Equal("42501", await RunAsync("DELETE FROM stock_ledger"));
        Assert.Equal("42501", await RunAsync("TRUNCATE stock_ledger"));
        Assert.Null(await RunAsync("UPDATE stock_items SET on_hand = on_hand"));
    }

    [Fact]
    public async Task Grants_follow_the_configured_role_name()
    {
        const string role = "inv_other";
        var owner = await containers.CreateDatabaseAsync();
        await using (var conn = new NpgsqlConnection(owner))
        {
            await conn.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"CREATE ROLE {role}", conn);
            await create.ExecuteNonQueryAsync(Ct);
        }

        var migrating = new NpgsqlConnectionStringBuilder(owner) { Options = $"-c inventory.app_role={role}" }.ConnectionString;
        await using (var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(migrating).Options))
            await db.Database.MigrateAsync(Ct);

        await using var check = new NpgsqlConnection(owner);
        await check.OpenAsync(Ct);
        async Task<bool> HasAsync(string privilege)
        {
            await using var cmd = new NpgsqlCommand($"SELECT has_table_privilege('{role}', 'stock_ledger', '{privilege}')", check);
            return (bool)(await cmd.ExecuteScalarAsync(Ct))!;
        }
        Assert.True(await HasAsync("INSERT"));
        Assert.True(await HasAsync("SELECT"));
        Assert.False(await HasAsync("UPDATE"));
        Assert.False(await HasAsync("DELETE"));
    }
}
