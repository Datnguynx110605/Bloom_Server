using Bloom.Payment.Contracts;
using Bloom.Payment.Data;
using Bloom.Payment.Errors;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;

namespace Bloom.Payment.Tests;

/// <summary>04-payment §6.1, §7, §8: the two-phase <c>CreatePayment</c> (PR-01, PR-02, PR-05) against real PostgreSQL and the fake Stripe.</summary>
public class CreatePaymentTests(Containers containers)
{
    private const string Sessions = "v1/checkout/sessions";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AsyncUnaryCall<CreatePaymentResponse> Create(TestPayment t, CreatePaymentRequest request) =>
        t.Internal.CreatePaymentAsync(request, cancellationToken: Ct);

    private static async Task<PaymentRow> RowAsync(TestPayment t, Guid orderId)
    {
        await using var db = t.NewDb();
        return await db.Payments.AsNoTracking().SingleAsync(p => p.OrderId == orderId, Ct);
    }

    private static async Task SqlAsync(TestPayment t, FormattableString sql)
    {
        await using var db = t.NewDb();
        await db.Database.ExecuteSqlAsync(sql, Ct);
    }

    private static IEnumerable<StripeRequest> Creates(TestPayment t) => t.Stripe.Requests.Where(r => r.Path == Sessions);

    [Fact]
    public async Task Create_opens_a_session_and_returns_it()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var start = t.Clock.GetUtcNow();

        var (orderId, response, sessionId) = await Seed.CreateAsync(t, 1000);

        var row = await RowAsync(t, orderId);
        Assert.Equal(PaymentStatuses.Open, row.Status);
        Assert.Equal(sessionId, row.StripeSessionId);
        Assert.Equal(response.PaymentId, row.Id.ToString("D"));
        Assert.Equal(t.Stripe.Session(sessionId).Url, row.CheckoutUrl);
        Assert.Equal(row.CheckoutUrl, response.CheckoutUrl);
        Assert.Equal("usd", row.Currency);
        Assert.Null(row.LockedUntil);
        Assert.Equal(start.AddMinutes(47).ToUnixTimeSeconds(), row.SessionExpiresAt!.Value.ToUnixTimeSeconds());
        Assert.Equal(row.SessionExpiresAt, response.SessionExpiresAt.ToDateTimeOffset());
        var request = Assert.Single(Creates(t));
        Assert.Equal("Device × 1", request.Form.Single(p => p.Key == "line_items[0][price_data][product_data][name]").Value);
        Assert.Equal("1000", request.Form.Single(p => p.Key == "line_items[0][price_data][unit_amount]").Value);
        Assert.Equal(1, metrics.Sum("payments_created_total", ("result", "created")));
        Assert.Empty(t.Stripe.Unserved);
    }

    [Fact]
    public async Task P01_create_twice_returns_the_same_session()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var (orderId, first, _) = await Seed.CreateAsync(t);

        var second = await Create(t, Seed.Request(orderId, 1000));

        Assert.Equal(first.PaymentId, second.PaymentId);
        Assert.Equal(first.CheckoutUrl, second.CheckoutUrl);
        Assert.Equal(1, t.Stripe.Count(Sessions));
        Assert.Equal(1, metrics.Sum("payments_created_total", ("result", "created")));
        Assert.Equal(1, metrics.Sum("payments_created_total", ("result", "existing")));
    }

    [Fact]
    public async Task P02_crash_after_stripe_create_resumes_after_60_s_with_the_same_session()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var orderId = Guid.NewGuid();
        t.Stripe.TimeoutNext(Sessions, storeFirst: true);
        await Rpc.FailsAsync(Create(t, Seed.Request(orderId, 1000)), PaymentErrors.ProviderUnavailable);
        Assert.Equal(PaymentStatuses.Creating, (await RowAsync(t, orderId)).Status);

        var locked = t.Clock.GetUtcNow().AddSeconds(60); // a crash never released the claim
        await SqlAsync(t, $"UPDATE payments SET locked_until = {locked} WHERE order_id = {orderId}");
        await Rpc.FailsAsync(Create(t, Seed.Request(orderId, 1000)), PaymentErrors.CreateInProgress);
        Assert.Equal(1, t.Stripe.Count(Sessions));

        t.Clock.Advance(TimeSpan.FromSeconds(61));
        var response = await Create(t, Seed.Request(orderId, 1000));

        var row = await RowAsync(t, orderId);
        Assert.Equal(PaymentStatuses.Open, row.Status);
        Assert.Equal("cs_test_1", row.StripeSessionId);
        Assert.Equal(row.CheckoutUrl, response.CheckoutUrl);
        var requests = Creates(t).ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].IdempotencyKey, requests[1].IdempotencyKey);
        Assert.Equal($"bloom:payment.create:{orderId:D}", requests[0].IdempotencyKey);
        Assert.Equal(requests[0].Form, requests[1].Form);
        Assert.Throws<KeyNotFoundException>(() => t.Stripe.Session("cs_test_2")); // one session only
    }

    [Fact]
    public async Task Immediate_retry_after_a_timeout_resumes()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var orderId = Guid.NewGuid();
        t.Stripe.TimeoutNext(Sessions, storeFirst: true);
        await Rpc.FailsAsync(Create(t, Seed.Request(orderId, 1000)), PaymentErrors.ProviderUnavailable);

        var response = await Create(t, Seed.Request(orderId, 1000)); // within 60 s, no clock change

        var row = await RowAsync(t, orderId);
        Assert.Equal(PaymentStatuses.Open, row.Status);
        Assert.Equal("cs_test_1", row.StripeSessionId);
        Assert.Equal(row.CheckoutUrl, response.CheckoutUrl);
        var requests = Creates(t).ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].Form, requests[1].Form); // a changed body would be an idempotency_error
        Assert.Empty(t.Stripe.Unserved);
    }

    [Fact]
    public async Task Stripe_error_is_PROVIDER_UNAVAILABLE_and_leaves_creating()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        var orderId = Guid.NewGuid();

        t.Stripe.Fail(Sessions, 500);
        await Rpc.FailsAsync(Create(t, Seed.Request(orderId, 1000)), PaymentErrors.ProviderUnavailable);
        var row = await RowAsync(t, orderId);
        Assert.Equal(PaymentStatuses.Creating, row.Status);
        Assert.True(row.LockedUntil <= t.Clock.GetUtcNow()); // released, the next call resumes (owner item 2)

        t.Stripe.Fail(Sessions, 400, "parameter_invalid_integer");
        await Rpc.FailsAsync(Create(t, Seed.Request(orderId, 1000)), PaymentErrors.ProviderUnavailable);
        Assert.Equal(PaymentStatuses.Creating, (await RowAsync(t, orderId)).Status);

        Assert.Equal(2, metrics.Sum("payments_created_total", ("result", "provider_unavailable")));
    }

    [Fact]
    public async Task Concurrent_creates_for_one_order_make_one_session()
    {
        await using var t = await TestPayment.StartAsync(containers);
        t.Stripe.Before = path => path == Sessions ? Task.Delay(300, Ct) : Task.CompletedTask;
        var orderId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            try { return (Response: await Create(t, Seed.Request(orderId, 1000)), Reason: (string?)null); }
            catch (RpcException e) { return (Response: (CreatePaymentResponse?)null, Reason: Rpc.Info(e).Reason); }
        }));

        var succeeded = results.Where(r => r.Response is not null).ToList();
        Assert.NotEmpty(succeeded);
        Assert.Single(succeeded.Select(r => r.Response!.CheckoutUrl).Distinct());
        Assert.Single(succeeded.Select(r => r.Response!.PaymentId).Distinct());
        Assert.All(results.Where(r => r.Response is null), r => Assert.Equal(PaymentErrors.CreateInProgress, r.Reason));
        await using var db = t.NewDb();
        Assert.Equal(1, await db.Payments.CountAsync(p => p.OrderId == orderId, Ct));
        Assert.Equal(1, t.Stripe.Count(Sessions));
    }

    [Fact]
    public async Task Different_amount_is_PAYMENT_EXISTS_DIFFERENT_AMOUNT()
    {
        await using var t = await TestPayment.StartAsync(containers);
        using var metrics = new MetricCapture(t.Services);
        string[] statuses = [PaymentStatuses.Open, PaymentStatuses.Succeeded, PaymentStatuses.Expired, PaymentStatuses.Canceled, PaymentStatuses.Creating];

        foreach (var status in statuses)
        {
            var (orderId, _, _) = await Seed.CreateAsync(t, 1000);
            var stale = t.Clock.GetUtcNow().AddSeconds(-1); // a stale `creating` row would be taken over: the amount is compared first (owner item 1)
            await SqlAsync(t, $"UPDATE payments SET status = {status}, locked_until = {(status == PaymentStatuses.Creating ? stale : (DateTimeOffset?)null)} WHERE order_id = {orderId}");

            await Rpc.FailsAsync(Create(t, Seed.Request(orderId, 2000)), PaymentErrors.ExistsDifferentAmount);

            Assert.Equal(status, (await RowAsync(t, orderId)).Status);
        }

        Assert.Equal(statuses.Length, t.Stripe.Count(Sessions)); // the seeds only
        Assert.Equal(statuses.Length, metrics.Sum("payments_created_total", ("result", "different_amount")));
    }

    [Fact]
    public async Task Expired_canceled_and_past_expiry_rows_are_returned_as_they_are()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var seeded = new List<(Guid OrderId, CreatePaymentResponse Response)>();
        foreach (var status in new[] { PaymentStatuses.Expired, PaymentStatuses.Canceled, PaymentStatuses.Open })
        {
            var (orderId, response, _) = await Seed.CreateAsync(t, 1000);
            await SqlAsync(t, $"UPDATE payments SET status = {status} WHERE order_id = {orderId}");
            seeded.Add((orderId, response));
        }
        t.Clock.Advance(TimeSpan.FromHours(2)); // the open row is now past session_expires_at

        foreach (var (orderId, first) in seeded)
        {
            var again = await Create(t, Seed.Request(orderId, 1000));

            Assert.Equal(first.PaymentId, again.PaymentId);
            Assert.Equal(first.CheckoutUrl, again.CheckoutUrl);
        }

        Assert.Equal(3, t.Stripe.Count(Sessions)); // PR-01: no new session
        Assert.Equal([PaymentStatuses.Expired, PaymentStatuses.Canceled, PaymentStatuses.Open], [.. await Task.WhenAll(seeded.Select(async s => (await RowAsync(t, s.OrderId)).Status))]);
    }

    [Fact]
    public async Task Validation_rules()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var id = Guid.NewGuid();
        var cases = new (string Field, CreatePaymentRequest Request)[]
        {
            ("order_id", Seed.Request(id, 1000).With(r => r.OrderId = "x")),
            ("account_id", Seed.Request(id, 1000).With(r => r.AccountId = "x")),
            ("amount_cents", Seed.Request(id, 0, ("Device × 1", 0))),
            ("display_lines", Seed.Request(id, 1000).With(r => r.DisplayLines.Clear())),
            ("display_lines", Seed.Request(id, 1000, ("A", 1100), ("B", -100))),
            ("display_lines", Seed.Request(id, 1000, ("A", 400), ("B", 500))),
        };

        foreach (var (field, request) in cases)
        {
            var info = await Rpc.FailsAsync(Create(t, request), PaymentErrors.ValidationFailed);
            Assert.Equal(field, info.Metadata["field"]);
        }

        await using var db = t.NewDb();
        Assert.Equal(0, await db.Payments.CountAsync(Ct));
        Assert.Equal(0, t.Stripe.Count(Sessions));
    }

    [Fact]
    public async Task Nothing_identifying_the_customer_is_stored_or_sent()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var request = Seed.Request(Guid.NewGuid(), 1000);
        await Create(t, request);

        Assert.Equal(request.AccountId, (await RowAsync(t, Guid.Parse(request.OrderId))).AccountId.ToString("D")); // PR-05: payments.account_id only
        var account = Guid.Parse(request.AccountId);
        Assert.All(t.Stripe.Requests, r =>
        {
            Assert.DoesNotContain(r.Form, p => p.Key.Contains("account", StringComparison.OrdinalIgnoreCase) || p.Value.Contains(account.ToString("N"), StringComparison.OrdinalIgnoreCase) || p.Value.Contains(request.AccountId, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(request.AccountId, r.IdempotencyKey ?? "");
        });
    }
}

file static class RequestExtensions
{
    public static CreatePaymentRequest With(this CreatePaymentRequest request, Action<CreatePaymentRequest> change)
    {
        change(request);
        return request;
    }
}
