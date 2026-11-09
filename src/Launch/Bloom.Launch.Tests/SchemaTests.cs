using Bloom.Launch.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Launch.Tests;

public class SchemaTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Id() => Guid.CreateVersion7();

    private static LaunchRow NewLaunch() => new()
    {
        Id = Id(),
        Name = "Drop",
        Stage = LaunchStages.Draft,
        LaunchTime = DateTimeOffset.UtcNow.AddDays(1),
        ReleaseDate = DateTimeOffset.UtcNow.AddDays(2),
        AdmissionRate = 10,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static HoldRow NewHold(Guid? orderId = null) => new()
    {
        Id = Id(),
        OrderId = orderId ?? Id(),
        LaunchId = Id(),
        AccountId = Id(),
        PassId = Id(),
        Status = HoldStatuses.Held,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>A launch with one product (limit 5) and one SKU (allocation 5).</summary>
    private static async Task<(LaunchRow Launch, LaunchProductRow Product, LaunchSkuRow Sku)> SeedAsync(LaunchDbContext db)
    {
        var launch = NewLaunch();
        var product = new LaunchProductRow { LaunchId = launch.Id, ProductId = Id(), PurchaseLimit = 5 };
        var sku = new LaunchSkuRow { LaunchId = launch.Id, SkuId = Id(), ProductId = product.ProductId, Allocation = 5 };
        db.AddRange(launch, product, sku);
        await db.SaveChangesAsync(Ct);
        return (launch, product, sku);
    }

    private static async Task<string> ViolationAsync(LaunchDbContext db, object entity, string sqlState)
    {
        db.Add(entity);
        var e = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        var pg = Assert.IsType<Npgsql.PostgresException>(e.InnerException);
        Assert.Equal(sqlState, pg.SqlState);
        return pg.ConstraintName!;
    }

    private static async Task<string> SqlViolationAsync(LaunchDbContext db, FormattableString sql, string sqlState)
    {
        var e = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlAsync(sql, Ct));
        Assert.Equal(sqlState, e.SqlState);
        return e.ConstraintName!;
    }

    [Fact]
    public async Task Migration_applies_and_model_has_no_pending_changes()
    {
        await using var t = await TestLaunch.StartAsync(containers);
        await using var db = t.NewDb();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));

        // Raw SQL that omits the columns with a §4.1 default gets those defaults.
        var (launch, product, sku) = await SeedAsync(db);
        var holdId = Id();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO limit_usage (launch_id, account_id, product_id) VALUES ({launch.Id}, {Id()}, {product.ProductId})
            """, Ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO holds (id, order_id, launch_id, account_id, pass_id, status, created_at, updated_at)
            VALUES ({holdId}, {Id()}, {launch.Id}, {Id()}, {Id()}, 'held', now(), now())
            """, Ct);
        var defaults = await db.Database.SqlQuery<string>($"""
            SELECT concat_ws(',', l.version, s.held, s.sold, u.held, u.sold, h.version) AS "Value"
            FROM launches l, launch_skus s, limit_usage u, holds h
            WHERE l.id = {launch.Id} AND s.sku_id = {sku.SkuId} AND u.launch_id = {launch.Id} AND h.id = {holdId}
            """).SingleAsync(Ct);
        Assert.Equal("0,0,0,0,0,0", defaults);

        // EF adds this index for the composite FK (its columns are not a prefix of the PK); it must follow the ix_ naming.
        Assert.Equal(1, await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pg_indexes
            WHERE tablename = 'launch_skus' AND indexname = 'ix_launch_skus_launch_id_product_id'
            """).SingleAsync(Ct));
    }

    [Fact]
    public async Task Held_plus_sold_above_allocation_is_rejected()
    {
        await using var t = await TestLaunch.StartAsync(containers);
        await using var db = t.NewDb();
        var (launch, product, _) = await SeedAsync(db);

        var bad = new LaunchSkuRow { LaunchId = launch.Id, SkuId = Id(), ProductId = product.ProductId, Allocation = 5, Held = 3, Sold = 3 };
        Assert.Equal("ck_launch_skus_within_allocation", await ViolationAsync(t.NewDb(), bad, "23514"));
    }

    [Fact]
    public async Task Negative_counters_are_rejected()
    {
        await using var t = await TestLaunch.StartAsync(containers);
        await using var db = t.NewDb();
        var (launch, product, sku) = await SeedAsync(db);

        Assert.Equal("ck_launch_skus_non_negative",
            await SqlViolationAsync(db, $"UPDATE launch_skus SET held = -1 WHERE sku_id = {sku.SkuId}", "23514"));
        Assert.Equal("ck_limit_usage_non_negative", await ViolationAsync(t.NewDb(),
            new LimitUsageRow { LaunchId = launch.Id, AccountId = Id(), ProductId = product.ProductId, Sold = -1 }, "23514"));
    }

    [Fact]
    public async Task A_sku_and_a_product_belong_to_one_launch_ever()
    {
        await using var t = await TestLaunch.StartAsync(containers);
        await using var db = t.NewDb();
        var (_, product, sku) = await SeedAsync(db);

        var other = NewLaunch();
        var otherProduct = new LaunchProductRow { LaunchId = other.Id, ProductId = Id(), PurchaseLimit = 5 };
        await using var db2 = t.NewDb();
        db2.AddRange(other, otherProduct);
        await db2.SaveChangesAsync(Ct);

        var sameSku = new LaunchSkuRow { LaunchId = other.Id, SkuId = sku.SkuId, ProductId = otherProduct.ProductId, Allocation = 1 };
        Assert.Equal("ux_launch_skus_sku_id", await ViolationAsync(t.NewDb(), sameSku, "23505"));
        var sameProduct = new LaunchProductRow { LaunchId = other.Id, ProductId = product.ProductId, PurchaseLimit = 1 };
        Assert.Equal("ux_launch_products_product_id", await ViolationAsync(t.NewDb(), sameProduct, "23505"));
    }

    [Fact]
    public async Task One_hold_per_order()
    {
        await using var t = await TestLaunch.StartAsync(containers);
        var first = NewHold();
        await using (var db = t.NewDb()) { db.Add(first); await db.SaveChangesAsync(Ct); }

        Assert.Equal("ux_holds_order_id", await ViolationAsync(t.NewDb(), NewHold(first.OrderId), "23505"));
    }

    [Fact]
    public async Task Sku_needs_its_product_in_the_same_launch()
    {
        await using var t = await TestLaunch.StartAsync(containers);
        await using var db = t.NewDb();
        var (launch, _, _) = await SeedAsync(db);

        var orphan = new LaunchSkuRow { LaunchId = launch.Id, SkuId = Id(), ProductId = Id(), Allocation = 1 };
        await ViolationAsync(t.NewDb(), orphan, "23503");
    }

    [Fact]
    public async Task Enum_and_rule_columns_reject_bad_values()
    {
        await using var t = await TestLaunch.StartAsync(containers);
        await using var db = t.NewDb();
        var (launch, product, _) = await SeedAsync(db);

        async Task<string> RejectAsync(object entity)
        {
            await using var d = t.NewDb();
            return await ViolationAsync(d, entity, "23514");
        }

        var l = NewLaunch(); l.Stage = "x";
        Assert.Equal("ck_launches_stage", await RejectAsync(l));
        l = NewLaunch(); l.ReleaseDate = l.LaunchTime;
        Assert.Equal("ck_launches_release_after_launch", await RejectAsync(l));
        l = NewLaunch(); l.AdmissionRate = 0;
        Assert.Equal("ck_launches_admission_rate", await RejectAsync(l));
        var h = NewHold(); h.Status = "x";
        Assert.Equal("ck_holds_status", await RejectAsync(h));

        var p = new LaunchProductRow { LaunchId = launch.Id, ProductId = Id(), PurchaseLimit = 0 };
        Assert.Equal("ck_launch_products_limit_positive", await RejectAsync(p));

        var hold = NewHold();
        await using (var d = t.NewDb()) { d.Add(hold); await d.SaveChangesAsync(Ct); }
        var line = new HoldLineRow { HoldId = hold.Id, SkuId = Id(), ProductId = product.ProductId, Quantity = 0 };
        Assert.Equal("ck_hold_lines_quantity_positive", await RejectAsync(line));
    }
}
