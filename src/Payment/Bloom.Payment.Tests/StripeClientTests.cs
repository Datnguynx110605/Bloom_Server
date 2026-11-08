using System.Diagnostics;
using Bloom.Payment.Stripe;
using Microsoft.Extensions.Time.Testing;

namespace Bloom.Payment.Tests;

/// <summary>The real <see cref="StripeClient"/> over <see cref="FakeStripe"/>; no containers (plan R10, R11).</summary>
public class StripeClientTests
{
    private const string Secret = "whsec_test";

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-11-08T10:00:00Z"));
    private readonly FakeStripe _fake;
    private readonly StripeClient _client;

    public StripeClientTests()
    {
        _fake = new FakeStripe(_clock);
        var options = new PaymentOptions
        {
            StorefrontBaseUrl = "https://shop.test/", // trailing slash: the URLs must not double it
            Stripe = { ApiKey = "sk_test_x", WebhookSecret = Secret, BaseUrl = "https://stripe.test/" },
        };
        var http = new HttpClient(_fake, disposeHandler: false);
        StripeClient.Configure(http, options.Stripe);
        _client = new StripeClient(http, options);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SessionRequest NewRequest(Guid? orderId = null) => new(
        orderId ?? Guid.NewGuid(), Guid.NewGuid(), "BL-1001", _clock.GetUtcNow().AddMinutes(45),
        [("Rose bouquet", 2500), ("Vase", 1500)]);

    private static string[] Keys(StripeRequest r) => [.. r.Form.Select(p => p.Key)];

    [Fact]
    public async Task Session_create_sends_the_documented_form_in_a_fixed_order()
    {
        var request = NewRequest();
        var session = await _client.CreateSessionAsync(request, Ct);

        var sent = Assert.Single(_fake.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("v1/checkout/sessions", sent.Path);
        string[] expected =
        [
            "mode", "client_reference_id", "success_url", "cancel_url", "expires_at", "custom_text[submit][message]",
            "metadata[order_id]", "metadata[payment_id]", "payment_intent_data[metadata][order_id]", "payment_intent_data[metadata][payment_id]",
            "line_items[0][price_data][currency]", "line_items[0][price_data][unit_amount]", "line_items[0][price_data][product_data][name]", "line_items[0][quantity]",
            "line_items[1][price_data][currency]", "line_items[1][price_data][unit_amount]", "line_items[1][price_data][product_data][name]", "line_items[1][quantity]",
        ];
        Assert.Equal(expected, Keys(sent));

        var form = sent.Form.ToDictionary(p => p.Key, p => p.Value);
        var order = request.OrderId.ToString("D");
        Assert.Equal("payment", form["mode"]);
        Assert.Equal(order, form["client_reference_id"]);
        Assert.Equal($"https://shop.test/orders/{order}", form["success_url"]);
        Assert.Equal($"https://shop.test/orders/{order}", form["cancel_url"]);
        Assert.Equal(request.ExpiresAt.ToUnixTimeSeconds().ToString(), form["expires_at"]);
        Assert.Equal("Order BL-1001", form["custom_text[submit][message]"]);
        Assert.Equal(request.PaymentId.ToString("D"), form["payment_intent_data[metadata][payment_id]"]);
        Assert.Equal(["usd", "1500"], [form["line_items[1][price_data][currency]"], form["line_items[1][price_data][unit_amount]"]]);
        Assert.Equal("Vase", form["line_items[1][price_data][product_data][name]"]);
        Assert.Equal("1", form["line_items[1][quantity]"]);

        Assert.Equal($"bloom:payment.create:{order}", sent.IdempotencyKey);
        Assert.Equal("Bearer sk_test_x", sent.Authorization);
        Assert.Equal("2024-06-20", sent.StripeVersion);

        Assert.StartsWith("cs_test_", session.Id);
        Assert.Equal("open", session.Status);
        Assert.Equal($"https://checkout.stripe.test/{session.Id}", session.Url);
        Assert.Equal(4000, session.AmountTotal);
        Assert.Equal(request.ExpiresAt.ToUnixTimeSeconds(), session.ExpiresAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Session_body_has_nothing_that_identifies_the_customer()
    {
        await _client.CreateSessionAsync(NewRequest(), Ct);

        string[] forbidden = ["email", "customer", "address", "phone", "name", "account"];
        foreach (var key in Keys(_fake.Requests[0]))
        {
            var isProductName = key.EndsWith("[product_data][name]", StringComparison.Ordinal);
            Assert.True(isProductName || !forbidden.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase)), $"'{key}' may identify the customer");
        }
    }

    [Fact]
    public async Task Same_request_twice_is_byte_identical()
    {
        var request = NewRequest();
        var first = await _client.CreateSessionAsync(request, Ct);
        var second = await _client.CreateSessionAsync(request, Ct);

        Assert.Equal(_fake.Requests[0].Form, _fake.Requests[1].Form);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, _fake.Count("v1/checkout/sessions"));
    }

    [Fact]
    public async Task Refund_create_uses_the_refund_key_and_metadata()
    {
        var session = await _client.CreateSessionAsync(NewRequest(), Ct);
        var pi = _fake.Pay(session.Id);
        var refundId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var refund = await _client.CreateRefundAsync(refundId, orderId, pi, 1000, Ct);

        var sent = _fake.Requests[^1];
        Assert.Equal("POST", sent.Method);
        Assert.Equal("v1/refunds", sent.Path);
        Assert.Equal($"bloom:refund:{refundId}", sent.IdempotencyKey);
        Assert.Equal(["payment_intent", "amount", "metadata[refund_id]", "metadata[order_id]"], Keys(sent));
        Assert.Equal([pi, "1000", refundId.ToString("D"), orderId.ToString("D")], sent.Form.Select(p => p.Value));
        Assert.StartsWith("re_test_", refund.Id);
        Assert.Equal("succeeded", refund.Status);
        Assert.Equal(1000, refund.Amount);
        Assert.Equal(refundId, refund.RefundId);

        Assert.Equal(refund, await _client.GetRefundAsync(refund.Id, Ct));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    public async Task Server_errors_and_429_are_transient(int status)
    {
        _fake.Fail("v1/checkout/sessions", status);

        var e = await Assert.ThrowsAsync<ProviderException>(() => _client.CreateSessionAsync(NewRequest(), Ct));

        Assert.True(e.Transient);
        Assert.Equal("provider_unavailable", e.Reason);
    }

    [Fact]
    public async Task A_4xx_is_permanent_with_stripes_code()
    {
        _fake.Fail("v1/checkout/sessions", 400, "resource_missing");
        var withCode = await Assert.ThrowsAsync<ProviderException>(() => _client.GetSessionAsync("cs_test_x", Ct));
        Assert.False(withCode.Transient);
        Assert.Equal("resource_missing", withCode.Reason);

        _fake.Fail("v1/checkout/sessions", 400);
        var withoutCode = await Assert.ThrowsAsync<ProviderException>(() => _client.GetSessionAsync("cs_test_x", Ct));
        Assert.False(withoutCode.Transient);
        Assert.Equal("invalid_request_error", withoutCode.Reason);
    }

    [Fact]
    public async Task Session_create_times_out_after_4_seconds()
    {
        _fake.TimeoutNext("v1/checkout/sessions", storeFirst: true);
        var request = NewRequest();

        var watch = Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<ProviderException>(() => _client.CreateSessionAsync(request, Ct));
        watch.Stop();

        Assert.True(e.Transient);
        Assert.Equal("provider_unavailable", e.Reason);
        Assert.InRange(watch.Elapsed.TotalSeconds, 3.5, 6);
        // Stripe stored the session before the timeout: the retry gets that very session back (spec §7).
        var retry = await _client.CreateSessionAsync(request, Ct);
        Assert.Equal("cs_test_1", retry.Id);
        Assert.Equal(2, _fake.Count("v1/checkout/sessions"));
    }

    [Fact]
    public async Task Lists_follow_every_page()
    {
        _fake.PageSize = 2;
        var from = _clock.GetUtcNow().AddHours(-1);
        var to = _clock.GetUtcNow().AddHours(1);
        for (var i = 0; i < 5; i++) _fake.AddDashboardRefund("pi_test_x", 100 + i, _clock.GetUtcNow().AddMinutes(i));
        _fake.AddDashboardRefund("pi_test_x", 999, to.AddHours(1)); // outside the range

        var refunds = await _client.ListRefundsAsync(from, to, Ct);

        Assert.Equal(5, refunds.Count);
        Assert.DoesNotContain(refunds, r => r.Amount == 999);
        var pages = _fake.Requests.Where(r => r.Method == "GET" && r.Path == "v1/refunds").ToList();
        Assert.Equal(3, pages.Count);
        Assert.Equal(["created[gte]", "created[lt]", "limit"], Keys(pages[0]));
        Assert.Equal("100", pages[0].Form[2].Value);
        Assert.Equal(["created[gte]", "created[lt]", "limit", "starting_after"], Keys(pages[1]));
        Assert.Equal(refunds[1].Id, pages[1].Form[3].Value);

        for (var i = 0; i < 3; i++) _fake.Pay((await _client.CreateSessionAsync(NewRequest(), Ct)).Id);
        var intents = await _client.ListPaymentIntentsAsync(from, to, Ct);
        Assert.Equal(3, intents.Count);
        Assert.All(intents, p => Assert.Equal("succeeded", p.Status));
    }

    private static string Header(string body, string secret, DateTimeOffset at) => StripeEvents.Sign(body, secret, at);

    [Fact]
    public void Signature_accepts_a_valid_header_and_any_matching_v1()
    {
        var now = _clock.GetUtcNow();
        var body = "{\"id\":\"evt_1\"}"u8.ToArray();
        var header = Header("{\"id\":\"evt_1\"}", Secret, now);
        Assert.True(WebhookSignature.Verify(header, body, Secret, now));

        var t = header.Split(',')[0];
        var good = header.Split(',')[1];
        Assert.True(WebhookSignature.Verify($"{t},v1=00ff,{good}", body, Secret, now));
        Assert.True(WebhookSignature.Verify(header, body, Secret, now.AddMinutes(5)));
    }

    [Fact]
    public void Signature_rejects_bad_mac_stale_or_future_t_and_garbage()
    {
        var now = _clock.GetUtcNow();
        var text = "{\"id\":\"evt_1\"}";
        var body = System.Text.Encoding.UTF8.GetBytes(text);

        Assert.False(WebhookSignature.Verify(Header(text, "whsec_other", now), body, Secret, now));
        Assert.False(WebhookSignature.Verify(Header(text, Secret, now.AddMinutes(-5).AddSeconds(-1)), body, Secret, now));
        Assert.False(WebhookSignature.Verify(Header(text, Secret, now.AddMinutes(5).AddSeconds(1)), body, Secret, now));
        Assert.False(WebhookSignature.Verify(null, body, Secret, now));
        Assert.False(WebhookSignature.Verify("", body, Secret, now));
        Assert.False(WebhookSignature.Verify("t=x,v1=zz", body, Secret, now));
        Assert.False(WebhookSignature.Verify($"t={now.ToUnixTimeSeconds()}", body, Secret, now)); // no v1
        Assert.False(WebhookSignature.Verify(Header(text, Secret, now), System.Text.Encoding.UTF8.GetBytes("{\"id\":\"evt_2\"}"), Secret, now));
    }
}
