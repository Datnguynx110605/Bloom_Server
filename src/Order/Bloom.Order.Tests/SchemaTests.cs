using Bloom.Order.Contracts;
using Bloom.Order.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Order.Tests;

public class SchemaTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Id() => Guid.CreateVersion7();

    private static string Number() => "BLM-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static OrderRow NewOrder(string? number = null) => new()
    {
        Id = Id(),
        Number = number ?? Number(),
        AccountId = Id(),
        Kind = OrderKinds.Regular,
        Status = OrderStatuses.PendingPayment,
        ShippingOption = ShippingOptions.Standard,
        ShipTo = new ShipToV1("Ann", ["1 Main St"], "Austin", "TX", "78701", "+15125550100"),
        Currency = "USD",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static OrderLine NewLine(Guid orderId) => new()
    {
        OrderId = orderId,
        LineNo = 1,
        Kind = LineKinds.Product,
        SkuId = Id(),
        ProductId = Id(),
        CategoryId = Id(),
        Name = "Phone",
        Quantity = 2,
        UnitPriceCents = 500,
        TaxCents = 0,
        PaidCents = 1000,
    };

    private static Coupon NewCoupon() => new()
    {
        Id = Id(),
        CodeNormalized = "SAVE" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
        CodeDisplay = "save",
        Type = CouponTypes.Percent,
        Value = 10,
        Scope = CouponScopes.Order,
        TotalLimit = 5,
        UsesCount = 5,
        Active = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<T> InsertAsync<T>(OrderDbContext db, T entity) where T : notnull
    {
        db.Add(entity);
        await db.SaveChangesAsync(Ct);
        return entity;
    }

    private static async Task<string> ViolationAsync(OrderDbContext db, object entity, string sqlState)
    {
        db.Add(entity);
        var e = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        var pg = Assert.IsType<Npgsql.PostgresException>(e.InnerException);
        Assert.Equal(sqlState, pg.SqlState);
        return pg.ConstraintName!;
    }

    [Fact]
    public async Task Migration_applies_and_model_has_no_pending_changes()
    {
        await using var t = await TestOrder.StartAsync(containers);
        await using var db = t.NewDb();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task Settings_are_seeded_with_a_15_dollar_express_fee()
    {
        await using var t = await TestOrder.StartAsync(containers);
        await using var db = t.NewDb();

        var row = Assert.Single(await db.CheckoutSettings.ToListAsync(Ct));
        Assert.Equal(1, row.Id);
        Assert.Equal(1500, row.ExpressFeeCents);
        Assert.Equal(0, row.Version);
        Assert.Null(row.UpdatedBy);

        await using var dup = t.NewDb();
        Assert.Equal("ck_checkout_settings_single_row", await ViolationAsync(dup, new CheckoutSettings { Id = 2, UpdatedAt = DateTimeOffset.UtcNow }, "23514"));
    }

    [Fact]
    public async Task Refund_above_paid_is_rejected()
    {
        await using var t = await TestOrder.StartAsync(containers);
        await using var db = t.NewDb();
        var order = await InsertAsync(db, NewOrder());
        await InsertAsync(db, NewLine(order.Id));

        var e = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE order_lines SET refunded_cents = paid_cents + 1 WHERE order_id = {order.Id}", Ct));
        Assert.Equal("23514", e.SqlState);
        Assert.Equal("ck_order_lines_refund_le_paid", e.ConstraintName);
    }

    [Fact]
    public async Task Coupon_uses_cannot_pass_the_total_limit()
    {
        await using var t = await TestOrder.StartAsync(containers);
        await using var db = t.NewDb();
        var coupon = await InsertAsync(db, NewCoupon());

        var e = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE coupons SET uses_count = total_limit + 1 WHERE id = {coupon.Id}", Ct));
        Assert.Equal("23514", e.SqlState);
        Assert.Equal("ck_coupons_uses_le_total_limit", e.ConstraintName);
    }

    [Fact]
    public async Task Order_number_and_checkout_key_are_unique()
    {
        await using var t = await TestOrder.StartAsync(containers);
        await using var db = t.NewDb();
        var first = await InsertAsync(db, NewOrder());
        var second = await InsertAsync(db, NewOrder());
        var key = await InsertAsync(db, new CheckoutKey { AccountId = first.AccountId, IdempotencyKey = Id(), OrderId = first.Id });

        await using var d1 = t.NewDb();
        Assert.Equal("ux_orders_number", await ViolationAsync(d1, NewOrder(first.Number), "23505"));
        await using var d2 = t.NewDb();
        Assert.Equal("PK_checkout_keys", await ViolationAsync(d2, new CheckoutKey { AccountId = key.AccountId, IdempotencyKey = key.IdempotencyKey, OrderId = second.Id }, "23505"));
        await using var d3 = t.NewDb();
        Assert.Equal("ux_checkout_keys_order_id", await ViolationAsync(d3, new CheckoutKey { AccountId = first.AccountId, IdempotencyKey = Id(), OrderId = first.Id }, "23505"));

        var stored = await t.NewDb().Orders.SingleAsync(o => o.Id == first.Id, Ct);
        Assert.Equal("Austin", stored.ShipTo.City);
    }

    [Fact]
    public async Task Enum_columns_reject_unknown_values()
    {
        await using var t = await TestOrder.StartAsync(containers);
        var order = await InsertAsync(t.NewDb(), NewOrder());
        var coupon = await InsertAsync(t.NewDb(), NewCoupon());

        async Task<string> RejectAsync(object entity)
        {
            await using var db = t.NewDb();
            return await ViolationAsync(db, entity, "23514");
        }

        var o = NewOrder(); o.Status = "x";
        Assert.Equal("ck_orders_status", await RejectAsync(o));
        o = NewOrder(); o.Kind = "x";
        Assert.Equal("ck_orders_kind", await RejectAsync(o));
        o = NewOrder(); o.CancelReason = "x";
        Assert.Equal("ck_orders_cancel_reason", await RejectAsync(o));
        o = NewOrder(); o.ShippingOption = "x";
        Assert.Equal("ck_orders_shipping_option", await RejectAsync(o));
        var line = NewLine(order.Id); line.Kind = "x";
        Assert.Equal("ck_order_lines_kind", await RejectAsync(line));
        var history = new OrderStatusHistory { OrderId = order.Id, ToStatus = "x", ActorType = ActorTypes.System, At = DateTimeOffset.UtcNow };
        Assert.Equal("ck_order_status_history_to_status", await RejectAsync(history));
        history = new OrderStatusHistory { OrderId = order.Id, FromStatus = "x", ToStatus = OrderStatuses.Paid, ActorType = ActorTypes.System, At = DateTimeOffset.UtcNow };
        Assert.Equal("ck_order_status_history_from_status", await RejectAsync(history));
        history = new OrderStatusHistory { OrderId = order.Id, ToStatus = OrderStatuses.Paid, ActorType = "x", At = DateTimeOffset.UtcNow };
        Assert.Equal("ck_order_status_history_actor_type", await RejectAsync(history));
        var saga = new OrderSaga { OrderId = order.Id, Type = "x", PendingSteps = [], StartedAt = DateTimeOffset.UtcNow };
        Assert.Equal("ck_order_sagas_type", await RejectAsync(saga));
        saga = new OrderSaga { OrderId = order.Id, Type = SagaTypes.Expiry, PendingSteps = [SagaSteps.Refund, "x"], StartedAt = DateTimeOffset.UtcNow };
        Assert.Equal("ck_order_sagas_pending_steps", await RejectAsync(saga));
        var cartLine = new CartLine { AccountId = Id(), LineNo = 1, Kind = "x", SkuId = Id(), Quantity = 1 };
        Assert.Equal("ck_cart_lines_kind", await RejectAsync(cartLine));
        var c = NewCoupon(); c.Type = "x";
        Assert.Equal("ck_coupons_type", await RejectAsync(c));
        c = NewCoupon(); c.Scope = "x";
        Assert.Equal("ck_coupons_scope", await RejectAsync(c));
        var item = new CouponScopeItem { CouponId = coupon.Id, TargetType = "x", TargetId = Id() };
        Assert.Equal("ck_coupon_scope_items_target_type", await RejectAsync(item));
        var use = new CouponUse { OrderId = order.Id, CouponId = coupon.Id, AccountId = order.AccountId, Status = "x", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        Assert.Equal("ck_coupon_uses_status", await RejectAsync(use));
    }
}
