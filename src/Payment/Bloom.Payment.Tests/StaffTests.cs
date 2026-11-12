using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bloom.Payment.Contracts;
using Bloom.Payment.Data;

namespace Bloom.Payment.Tests;

/// <summary>04-payment §5.1 <c>GetPayment</c> over transcoded HTTP (spec §4, §6): staff only (Support, Admin), R7, R9.</summary>
public class StaffTests(Containers containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> GetAsync(TestPayment t, string orderId, string userType, string? role)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/admin/payments/{orderId}");
        request.Headers.Add("x-bloom-user-id", Guid.NewGuid().ToString("D"));
        request.Headers.Add("x-bloom-user-type", userType);
        if (role is not null) request.Headers.Add("x-bloom-role", role);
        return t.Http.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ErrorInfoAsync(HttpResponseMessage response, int status, string reason)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(status == (int)response.StatusCode, $"Expected HTTP {status} but got {(int)response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        var info = doc.RootElement.GetProperty("details").EnumerateArray()
            .Single(d => d.GetProperty("@type").GetString() == "type.googleapis.com/google.rpc.ErrorInfo").Clone();
        Assert.Equal("bloom", info.GetProperty("domain").GetString());
        Assert.Equal(reason, info.GetProperty("reason").GetString());
        return info;
    }

    [Fact]
    public async Task Support_reads_payment_refunds_and_disputes()
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, paymentId, piId) = await Seed.PaidAsync(t, 1000);
        var now = t.Clock.GetUtcNow();
        var (older, newer) = (Guid.NewGuid(), Guid.NewGuid());
        await using (var db = t.NewDb())
        {
            // inserted newest first: the response is ordered by created_at, not by insertion
            db.Refunds.Add(new RefundRow
            {
                Id = newer, PaymentId = paymentId, OrderId = orderId, AmountCents = 200, Reason = "delivery_failure", Status = RefundStatuses.Pending,
                NextAttemptAt = now, CreatedAt = now.AddMinutes(1), UpdatedAt = now.AddMinutes(1),
            });
            db.Refunds.Add(new RefundRow
            {
                Id = older, PaymentId = paymentId, OrderId = orderId, AmountCents = 300, Reason = "return", Status = RefundStatuses.Succeeded, StripeRefundId = "re_1",
                Lines = [new RefundLineV1(1, 100), new RefundLineV1(2, 200)], CreatedAt = now, UpdatedAt = now,
            });
            db.Disputes.Add(new DisputeRow { Id = Guid.NewGuid(), PaymentId = paymentId, StripeDisputeId = "dp_2", Status = "won", AmountCents = 1000, Reason = "general", OpenedAt = now.AddMinutes(1), UpdatedAt = now.AddMinutes(2) });
            db.Disputes.Add(new DisputeRow { Id = Guid.NewGuid(), PaymentId = paymentId, StripeDisputeId = "dp_1", Status = "needs_response", AmountCents = 1000, Reason = "fraudulent", OpenedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(Ct);
        }

        using var response = await GetAsync(t, orderId.ToString("D"), "staff", "support");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var payment = body.GetProperty("payment");
        Assert.Equal(paymentId.ToString("D"), payment.GetProperty("id").GetString());
        Assert.Equal(orderId.ToString("D"), payment.GetProperty("orderId").GetString());
        Assert.Equal("succeeded", payment.GetProperty("status").GetString());
        Assert.Equal("usd", payment.GetProperty("currency").GetString());
        Assert.Equal(JsonValueKind.String, payment.GetProperty("amountCents").ValueKind); // int64 is a JSON string
        Assert.Equal("1000", payment.GetProperty("amountCents").GetString());
        Assert.Equal(piId, payment.GetProperty("stripePaymentIntentId").GetString());
        Assert.StartsWith("cs_", payment.GetProperty("stripeSessionId").GetString());
        Assert.False(payment.GetProperty("cancelRequested").GetBoolean());
        Assert.NotEqual(JsonValueKind.Undefined, payment.GetProperty("succeededAt").ValueKind);
        Assert.False(payment.TryGetProperty("lockedUntil", out _));

        var refunds = body.GetProperty("refunds").EnumerateArray().ToList();
        Assert.Equal([older.ToString("D"), newer.ToString("D")], refunds.Select(r => r.GetProperty("id").GetString()));
        Assert.Equal("re_1", refunds[0].GetProperty("stripeRefundId").GetString());
        Assert.Equal("300", refunds[0].GetProperty("amountCents").GetString());
        Assert.Equal(["1:100", "2:200"], refunds[0].GetProperty("lines").EnumerateArray().Select(l => $"{l.GetProperty("lineNo").GetInt32()}:{l.GetProperty("amountCents").GetString()}"));
        Assert.Empty(refunds[1].GetProperty("lines").EnumerateArray()); // null lines: an empty list
        Assert.Equal("", refunds[1].GetProperty("stripeRefundId").GetString()); // null text: ""
        Assert.False(refunds[0].TryGetProperty("nextAttemptAt", out _)); // null time: unset
        Assert.True(refunds[1].TryGetProperty("nextAttemptAt", out _));
        Assert.Equal(["dp_1", "dp_2"], body.GetProperty("disputes").EnumerateArray().Select(d => d.GetProperty("stripeDisputeId").GetString()));
    }

    [Fact]
    public async Task Unknown_order_is_NOT_FOUND()
    {
        await using var t = await TestPayment.StartAsync(containers);

        using var unknown = await GetAsync(t, Guid.NewGuid().ToString("D"), "staff", "admin");
        await ErrorInfoAsync(unknown, 404, "NOT_FOUND");

        using var malformed = await GetAsync(t, "x", "staff", "admin");
        var info = await ErrorInfoAsync(malformed, 400, "VALIDATION_FAILED");
        Assert.Equal("order_id", info.GetProperty("metadata").GetProperty("field").GetString());
    }

    [Theory, InlineData("warehouse"), InlineData("catalog_manager")]
    public async Task Other_roles_are_ROLE_NOT_ALLOWED(string role)
    {
        await using var t = await TestPayment.StartAsync(containers);
        var (orderId, _, _) = await Seed.PaidAsync(t, 1000);

        using var staff = await GetAsync(t, orderId.ToString("D"), "staff", role);
        using var customer = await GetAsync(t, orderId.ToString("D"), "customer", null);
        using var malformed = await GetAsync(t, "x", "staff", role); // the role check runs before validation (R7)

        await ErrorInfoAsync(staff, 403, "ROLE_NOT_ALLOWED");
        await ErrorInfoAsync(customer, 403, "ROLE_NOT_ALLOWED");
        await ErrorInfoAsync(malformed, 403, "ROLE_NOT_ALLOWED");
    }
}
