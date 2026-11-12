using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Bloom.Payment.Stripe;

/// <summary>Plan R10: <c>HttpClient</c> with form-encoded requests (ordered pairs) and <c>System.Text.Json</c> answers. Configure the client with <see cref="Configure"/>.</summary>
public sealed class StripeClient(HttpClient http, PaymentOptions options) : IPaymentProvider
{
    public const string ApiVersion = "2024-06-20";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CreateSessionTimeout = TimeSpan.FromSeconds(4); // fits order's 5 s deadline

    /// <summary>Base address, bearer key, pinned <c>Stripe-Version</c> and the 10 s timeout.</summary>
    public static void Configure(HttpClient http, PaymentOptions.StripeOptions stripe)
    {
        http.BaseAddress = new Uri(stripe.BaseUrl.TrimEnd('/') + "/");
        http.Timeout = Timeout;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", stripe.ApiKey);
        http.DefaultRequestHeaders.Add("Stripe-Version", ApiVersion);
    }

    public async Task<StripeSession> CreateSessionAsync(SessionRequest request, CancellationToken ct)
    {
        var order = request.OrderId.ToString("D");
        var payment = request.PaymentId.ToString("D");
        var orderUrl = $"{options.StorefrontBaseUrl.TrimEnd('/')}/orders/{order}";
        // The order of these pairs is part of the idempotent request: a resume must send byte-identical bytes (spec §7).
        List<(string, string)> form =
        [
            ("mode", "payment"),
            ("client_reference_id", order),
            ("success_url", orderUrl),
            ("cancel_url", orderUrl),
            ("expires_at", request.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ("custom_text[submit][message]", $"Order {request.OrderNumber}"),
            ("metadata[order_id]", order),
            ("metadata[payment_id]", payment),
            ("payment_intent_data[metadata][order_id]", order),
            ("payment_intent_data[metadata][payment_id]", payment),
        ];
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = $"line_items[{i}]";
            form.Add(($"{line}[price_data][currency]", "usd"));
            form.Add(($"{line}[price_data][unit_amount]", request.Lines[i].AmountCents.ToString(CultureInfo.InvariantCulture)));
            form.Add(($"{line}[price_data][product_data][name]", request.Lines[i].Name));
            form.Add(($"{line}[quantity]", "1"));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CreateSessionTimeout);
        return await SendAsync(HttpMethod.Post, "v1/checkout/sessions", form, $"bloom:payment.create:{order}", Session, ct, timeout.Token);
    }

    public Task<StripeSession> GetSessionAsync(string sessionId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"v1/checkout/sessions/{Uri.EscapeDataString(sessionId)}", null, null, Session, ct);

    public Task<StripeSession> ExpireSessionAsync(string sessionId, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"v1/checkout/sessions/{Uri.EscapeDataString(sessionId)}/expire", [], null, Session, ct);

    public Task<StripeRefund> CreateRefundAsync(Guid refundId, Guid orderId, string paymentIntentId, long amountCents, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "v1/refunds",
            [
                ("payment_intent", paymentIntentId),
                ("amount", amountCents.ToString(CultureInfo.InvariantCulture)),
                ("metadata[refund_id]", refundId.ToString("D")),
                ("metadata[order_id]", orderId.ToString("D")),
            ],
            $"bloom:refund:{refundId:D}", Refund, ct);

    public Task<StripeRefund> GetRefundAsync(string stripeRefundId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"v1/refunds/{Uri.EscapeDataString(stripeRefundId)}", null, null, Refund, ct);

    public Task<IReadOnlyList<StripeRefund>> ListRefundsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        ListAsync("v1/refunds", from, to, Refund, ct);

    public Task<IReadOnlyList<StripePaymentIntent>> ListPaymentIntentsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        ListAsync("v1/payment_intents", from, to, PaymentIntent, ct);

    private async Task<IReadOnlyList<T>> ListAsync<T>(string path, DateTimeOffset from, DateTimeOffset to, Func<JsonElement, T> read, CancellationToken ct) where T : class
    {
        var all = new List<T>();
        string? after = null;
        do
        {
            var query = $"created[gte]={from.ToUnixTimeSeconds()}&created[lt]={to.ToUnixTimeSeconds()}&limit=100"
                + (after is null ? "" : $"&starting_after={Uri.EscapeDataString(after)}");
            (var items, after) = await SendAsync(HttpMethod.Get, $"{path}?{query}", null, null, page =>
            {
                var data = page.GetProperty("data").EnumerateArray().ToList();
                // P-7: a page that says there is more but has no last item would end the list early, or crash on data[^1].
                var next = page.GetProperty("has_more").GetBoolean()
                    ? Id(data.Count > 0 ? data[^1] : throw new InvalidOperationException("Stripe sent an empty page with has_more."))
                    : null;
                return (data.Select(read).ToList(), next);
            }, ct);
            all.AddRange(items);
        }
        while (after is not null);
        return all;
    }

    /// <summary>One Stripe call. Whatever goes wrong becomes a <see cref="ProviderException"/>; the caller's own cancellation is not wrapped.</summary>
    private async Task<T> SendAsync<T>(HttpMethod method, string url, List<(string Key, string Value)>? form, string? idempotencyKey, Func<JsonElement, T> read,
        CancellationToken ct, CancellationToken? sendToken = null)
    {
        try
        {
            using var message = new HttpRequestMessage(method, url);
            if (form is not null) message.Content = new FormUrlEncodedContent(form.Select(p => KeyValuePair.Create(p.Key, p.Value)));
            if (idempotencyKey is not null) message.Headers.Add("Idempotency-Key", idempotencyKey);
            using var response = await http.SendAsync(message, sendToken ?? ct);
            var body = await response.Content.ReadAsStringAsync(sendToken ?? ct);
            if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, body, method, url);
            using var json = JsonDocument.Parse(body);
            return read(json.RootElement);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        {
            throw new ProviderException(true, ProviderException.Unavailable, $"Stripe call {method} {url} failed: {e.Message}", e);
        }
    }

    private static ProviderException Failure(HttpStatusCode status, string body, HttpMethod method, string url)
    {
        var message = $"Stripe answered {(int)status} to {method} {url}.";
        // 409: Stripe answers it while the same idempotency key is still in flight (04-payment §6.3, D24), so the retry gets the same refund.
        if ((int)status >= 500 || status is HttpStatusCode.TooManyRequests or HttpStatusCode.Conflict) return new(true, ProviderException.Unavailable, message);
        string? reason = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            var error = json.RootElement.GetProperty("error");
            reason = Text(error, "code") ?? Text(error, "type");
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { } // no readable error object: fall back to the status
        return new(false, reason ?? $"http_{(int)status}", message);
    }

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Id(JsonElement e) => Text(e, "id") ?? throw new InvalidOperationException("A Stripe object has no id.");

    private static DateTimeOffset Unix(JsonElement e, string name) => DateTimeOffset.FromUnixTimeSeconds(e.GetProperty(name).GetInt64());

    private static StripeSession Session(JsonElement s) => new(
        Id(s), Text(s, "url"), s.GetProperty("status").GetString()!, Text(s, "payment_status"), Text(s, "payment_intent"),
        s.TryGetProperty("amount_total", out var total) && total.ValueKind == JsonValueKind.Number ? total.GetInt64() : null, Unix(s, "expires_at"));

    private static StripeRefund Refund(JsonElement r) => new(
        Id(r), r.GetProperty("status").GetString()!, r.GetProperty("amount").GetInt64(), Text(r, "failure_reason"),
        r.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object && Text(m, "refund_id") is { } id && Guid.TryParse(id, out var refundId) ? refundId : null,
        Unix(r, "created"));

    private static StripePaymentIntent PaymentIntent(JsonElement p) => new(
        Id(p), p.GetProperty("status").GetString()!, p.GetProperty("amount").GetInt64(), Unix(p, "created"));
}
