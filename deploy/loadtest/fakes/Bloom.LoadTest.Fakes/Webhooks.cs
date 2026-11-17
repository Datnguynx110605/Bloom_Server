using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Bloom.LoadTest.Fakes;

/// <summary>
/// Plays the customer: for each queued session, after <c>PayDelayMs</c> it pays the session and POSTs a signed
/// <c>checkout.session.completed</c> to <c>WebhookUrl</c>. A non-2xx answer or an exception is retried after 1 s, 2 s, 4 s, then counted as failed.
/// </summary>
public sealed class Webhooks(StripeFake stripe, IHttpClientFactory http, IConfiguration config) : BackgroundService
{
    public const string ClientName = "webhooks";

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly double _payFraction = config.GetValue("Fakes:Stripe:PayFraction", 1.0);
    private readonly int _delayMs = config.GetValue("Fakes:Stripe:PayDelayMs", 2000);
    private readonly int _ips = config.GetValue("Fakes:Stripe:WebhookIps", 12);
    private readonly string? _url = config["Fakes:Stripe:WebhookUrl"];
    private readonly string _secret = config["Fakes:Stripe:WebhookSecret"] ?? "";
    private int _next, _delivered, _failed;

    public int Delivered => Volatile.Read(ref _delivered);

    public int Failed => Volatile.Read(ref _failed);

    /// <summary>Queues a new session for payment, with probability <c>PayFraction</c>.</summary>
    public void Offer(string sessionId)
    {
        if (Random.Shared.NextDouble() < _payFraction) _queue.Writer.TryWrite(sessionId);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(ct))
            _ = DeliverAsync(id, ct); // one task per session, so the delay of one never holds back another
    }

    private async Task DeliverAsync(string id, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_delayMs, ct);
            if (stripe.Pay(id) is not { } s) return;
            var body = JsonSerializer.Serialize(new
            {
                id = $"evt_test_{Guid.NewGuid()}", @object = "event", type = "checkout.session.completed", created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                data = new
                {
                    @object = new
                    {
                        id = s.Id, @object = "checkout.session", status = s.Status, payment_status = s.PaymentStatus, payment_intent = s.PaymentIntentId,
                        metadata = new { order_id = s.OrderId, payment_id = s.PaymentId },
                    },
                },
            });
            var ip = $"100.127.0.{(Interlocked.Increment(ref _next) - 1) % _ips + 1}";
            for (var attempt = 0; ; attempt++)
            {
                if (await PostAsync(body, ip, ct)) { Interlocked.Increment(ref _delivered); return; }
                if (attempt == Backoff.Length) { Interlocked.Increment(ref _failed); return; }
                await Task.Delay(Backoff[attempt], ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { } // shutting down
    }

    private async Task<bool> PostAsync(string body, string ip, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("Stripe-Signature", Sign(body, _secret, DateTimeOffset.UtcNow));
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
            using var response = await http.CreateClient(ClientName).SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>The <c>Stripe-Signature</c> header: <c>t=unix,v1=hex HMAC-SHA256 of "t.body"</c>.</summary>
    private static string Sign(string body, string secret, DateTimeOffset at)
    {
        var t = at.ToUnixTimeSeconds();
        return $"t={t},v1={Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}")))}";
    }
}
