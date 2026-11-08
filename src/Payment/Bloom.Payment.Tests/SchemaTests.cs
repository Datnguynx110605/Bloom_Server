using Bloom.Payment.Contracts;
using Bloom.Payment.Data;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Payment.Tests;

public class SchemaTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Guid Id() => Guid.CreateVersion7();

    private static PaymentRow NewPayment(Guid? orderId = null) => new()
    {
        Id = Id(),
        OrderId = orderId ?? Id(),
        OrderNumber = "BLM-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
        AccountId = Id(),
        AmountCents = 1000,
        Currency = "usd",
        Status = PaymentStatuses.Creating,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static RefundRow NewRefund(Guid paymentId) => new()
    {
        Id = Id(),
        PaymentId = paymentId,
        OrderId = Id(),
        AmountCents = 100,
        Reason = "return",
        Status = RefundStatuses.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static DisputeRow NewDispute(Guid paymentId) => new()
    {
        Id = Id(),
        PaymentId = paymentId,
        StripeDisputeId = "dp_" + Id().ToString("N"),
        Status = "needs_response",
        AmountCents = 100,
        Reason = "fraudulent",
        OpenedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<T> InsertAsync<T>(PaymentDbContext db, T entity) where T : notnull
    {
        db.Add(entity);
        await db.SaveChangesAsync(Ct);
        return entity;
    }

    private static async Task<string> ViolationAsync(PaymentDbContext db, object entity, string sqlState)
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
        await using var t = await TestPayment.StartAsync(containers);
        await using var db = t.NewDb();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
    }

    [Fact]
    public async Task Refund_total_above_amount_is_rejected()
    {
        await using var t = await TestPayment.StartAsync(containers);
        await using var db = t.NewDb();
        var payment = await InsertAsync(db, NewPayment());

        // Raw SQL that omits the columns with a §4 default gets those defaults.
        var raw = Id();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO payments (id, order_id, order_number, account_id, amount_cents, currency, status, created_at, updated_at)
            VALUES ({raw}, {Id()}, 'BLM-RAW', {Id()}, 500, 'usd', 'creating', now(), now())
            """, Ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO refunds (id, payment_id, order_id, amount_cents, reason, status, created_at, updated_at)
            VALUES ({Id()}, {raw}, {Id()}, 100, 'return', 'pending', now(), now())
            """, Ct);
        var defaults = await db.Database.SqlQuery<string>($"""
            SELECT concat_ws(',', p.cancel_requested, p.refunded_cents, p.version, r.attempts) AS "Value"
            FROM payments p JOIN refunds r ON r.payment_id = p.id WHERE p.id = {raw}
            """).SingleAsync(Ct);
        Assert.Equal("f,0,0,0", defaults);

        var e = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE payments SET refunded_cents = amount_cents + 1 WHERE id = {payment.Id}", Ct));
        Assert.Equal("23514", e.SqlState);
        Assert.Equal("ck_payments_refunded_le_amount", e.ConstraintName);
    }

    [Fact]
    public async Task One_payment_per_order()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var first = await InsertAsync(t.NewDb(), NewPayment());

        await using var db = t.NewDb();
        Assert.Equal("ux_payments_order_id", await ViolationAsync(db, NewPayment(first.OrderId), "23505"));
    }

    [Fact]
    public async Task Stripe_ids_are_unique()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var p = NewPayment(); p.StripeSessionId = "cs_1"; p.StripePaymentIntentId = "pi_1";
        await InsertAsync(t.NewDb(), p);
        var r = NewRefund(p.Id); r.StripeRefundId = "re_1"; r.Lines = [new(1, 100)];
        await InsertAsync(t.NewDb(), r);
        var d = NewDispute(p.Id);
        await InsertAsync(t.NewDb(), d);

        async Task<string> RejectAsync(object entity)
        {
            await using var db = t.NewDb();
            return await ViolationAsync(db, entity, "23505");
        }

        var dup = NewPayment(); dup.StripeSessionId = "cs_1";
        Assert.Equal("ux_payments_stripe_session_id", await RejectAsync(dup));
        dup = NewPayment(); dup.StripePaymentIntentId = "pi_1";
        Assert.Equal("ux_payments_stripe_payment_intent_id", await RejectAsync(dup));
        var dupRefund = NewRefund(p.Id); dupRefund.StripeRefundId = "re_1";
        Assert.Equal("ux_refunds_stripe_refund_id", await RejectAsync(dupRefund));
        var dupDispute = NewDispute(p.Id); dupDispute.StripeDisputeId = d.StripeDisputeId;
        Assert.Equal("ux_disputes_stripe_dispute_id", await RejectAsync(dupDispute));

        // Two rows with null ids are fine, and a refund's lines round-trip through jsonb.
        await InsertAsync(t.NewDb(), NewPayment());
        await InsertAsync(t.NewDb(), NewPayment());
        await InsertAsync(t.NewDb(), NewRefund(p.Id));
        await InsertAsync(t.NewDb(), NewRefund(p.Id));
        var stored = await t.NewDb().Refunds.SingleAsync(x => x.Id == r.Id, Ct);
        Assert.Equal([new RefundLineV1(1, 100)], stored.Lines);
    }

    [Fact]
    public async Task Enum_columns_reject_unknown_values()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var payment = await InsertAsync(t.NewDb(), NewPayment());

        async Task<string> RejectAsync(object entity)
        {
            await using var db = t.NewDb();
            return await ViolationAsync(db, entity, "23514");
        }

        var p = NewPayment(); p.Status = "x";
        Assert.Equal("ck_payments_status", await RejectAsync(p));
        p = NewPayment(); p.Currency = "eur";
        Assert.Equal("ck_payments_currency", await RejectAsync(p));
        p = NewPayment(); p.AmountCents = 0;
        Assert.Equal("ck_payments_amount_positive", await RejectAsync(p));
        p = NewPayment(); p.RefundedCents = -1;
        Assert.Equal("ck_payments_refunded_non_negative", await RejectAsync(p));
        var r = NewRefund(payment.Id); r.Status = "x";
        Assert.Equal("ck_refunds_status", await RejectAsync(r));
        r = NewRefund(payment.Id); r.AmountCents = 0;
        Assert.Equal("ck_refunds_amount_positive", await RejectAsync(r));
        r = NewRefund(payment.Id); r.Attempts = -1;
        Assert.Equal("ck_refunds_attempts_non_negative", await RejectAsync(r));
    }
}
