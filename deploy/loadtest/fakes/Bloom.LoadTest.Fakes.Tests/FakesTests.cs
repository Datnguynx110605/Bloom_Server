using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Bloom.Payment.Stripe;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Bloom.LoadTest.Fakes.Tests;

public sealed class FakesTests
{
    private const string Secret = "whsec_lt";
    private const string ClientId = "lt-client";

    /// <summary>A webhook endpoint on Kestrel 127.0.0.1:0 that records each POST and answers <c>status</c>.</summary>
    private sealed class Receiver(WebApplication app, List<(string Body, IReadOnlyDictionary<string, string> Headers)> posts) : IAsyncDisposable
    {
        public string Url => app.Urls.First() + "/hook";

        public int Count { get { lock (posts) return posts.Count; } }

        public (string Body, IReadOnlyDictionary<string, string> Headers) Wait(int n, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (Count < n && DateTime.UtcNow < until) Thread.Sleep(20);
            lock (posts) return posts.Count >= n ? posts[n - 1] : throw new TimeoutException($"{n} webhook(s) not received");
        }

        public static async Task<Receiver> StartAsync(int status = 200)
        {
            var posts = new List<(string, IReadOnlyDictionary<string, string>)>();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.MapPost("/hook", async (HttpRequest r) =>
            {
                var body = await new StreamReader(r.Body).ReadToEndAsync();
                lock (posts) posts.Add((body, r.Headers.ToDictionary(h => h.Key, h => h.Value.ToString())));
                return Results.StatusCode(status);
            });
            await app.StartAsync(Ct);
            return new(app, posts);
        }

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }

    private static async Task<(WebApplication App, HttpClient Http)> StartFakesAsync(string? webhookUrl = null, string payFraction = "1", string payDelayMs = "0")
    {
        var app = FakesApp.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Logging.ClearProviders();
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fakes:Stripe:WebhookUrl"] = webhookUrl,
                ["Fakes:Stripe:WebhookSecret"] = Secret,
                ["Fakes:Stripe:PayFraction"] = payFraction,
                ["Fakes:Stripe:PayDelayMs"] = payDelayMs,
                ["Fakes:Google:ClientId"] = ClientId,
            });
        });
        await app.StartAsync(Ct);
        return (app, app.GetTestClient());
    }

    private static Task<HttpResponseMessage> CreateSession(HttpClient http, string key, long expiresAt, string orderId = "o-1", string paymentId = "p-1") =>
        http.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/checkout/sessions")
        {
            Headers = { { "Idempotency-Key", key } },
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["expires_at"] = expiresAt.ToString(),
                ["line_items[0][price_data][unit_amount]"] = "1500",
                ["line_items[0][quantity]"] = "2",
                ["metadata[order_id]"] = orderId,
                ["metadata[payment_id]"] = paymentId,
            }),
        });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long In(int minutes) => DateTimeOffset.UtcNow.AddMinutes(minutes).ToUnixTimeSeconds();

    private static async Task<JsonNode> Json(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonNode>(Ct))!;

    private static async Task<int> Stat(HttpClient http, string name) => (int)(await Json(await http.GetAsync("/_stats", Ct)))[name]!;

    [Fact]
    public async Task CreateSession_rejects_expiry_under_30_minutes()
    {
        var (app, http) = await StartFakesAsync();
        await using var _ = app;
        var r = await CreateSession(http, "k1", In(29));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("parameter_invalid_integer", (string?)(await Json(r))["error"]!["code"]);
    }

    [Fact]
    public async Task CreateSession_replays_same_idempotency_key()
    {
        var (app, http) = await StartFakesAsync(payFraction: "0");
        await using var _ = app;
        var expires = In(31);
        var first = await Json(await CreateSession(http, "k1", expires));
        var second = await Json(await CreateSession(http, "k1", expires));
        Assert.Equal((string?)first["id"], (string?)second["id"]);
        Assert.Equal(1, await Stat(http, "sessionsCreated"));
    }

    [Fact]
    public async Task CreateSession_same_key_other_body_is_idempotency_error()
    {
        var (app, http) = await StartFakesAsync(payFraction: "0");
        await using var _ = app;
        await CreateSession(http, "k1", In(31), orderId: "o-1");
        var r = await CreateSession(http, "k1", In(31), orderId: "o-2");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("idempotency_error", (string?)(await Json(r))["error"]!["type"]);
    }

    [Fact]
    public async Task Paid_session_posts_signed_completed_webhook()
    {
        await using var receiver = await Receiver.StartAsync();
        var (app, http) = await StartFakesAsync(receiver.Url);
        await using var _ = app;
        var id = (string?)(await Json(await CreateSession(http, "k1", In(31), orderId: "order-42", paymentId: "pay-7")))["id"];

        var (body, headers) = receiver.Wait(1, TimeSpan.FromSeconds(5));

        Assert.True(WebhookSignature.Verify(headers["Stripe-Signature"], Encoding.UTF8.GetBytes(body), Secret, DateTimeOffset.UtcNow));
        var evt = JsonNode.Parse(body)!;
        Assert.Equal("checkout.session.completed", (string?)evt["type"]);
        Assert.Equal("order-42", (string?)evt["data"]!["object"]!["metadata"]!["order_id"]);
        Assert.StartsWith("100.127.0.", headers["X-Forwarded-For"].ToString());
        Assert.Equal("complete", (string?)(await Json(await http.GetAsync($"/v1/checkout/sessions/{id}", Ct)))["status"]);
    }

    [Fact]
    public async Task PayFraction_zero_sends_no_webhook()
    {
        await using var receiver = await Receiver.StartAsync();
        var (app, http) = await StartFakesAsync(receiver.Url, payFraction: "0");
        await using var _ = app;
        var id = (string?)(await Json(await CreateSession(http, "k1", In(31))))["id"];

        await Task.Delay(1000, Ct);

        Assert.Equal(0, receiver.Count);
        Assert.Equal("open", (string?)(await Json(await http.GetAsync($"/v1/checkout/sessions/{id}", Ct)))["status"]);
    }

    [Fact]
    public async Task Undeliverable_webhook_is_counted_after_retries()
    {
        await using var receiver = await Receiver.StartAsync(429);
        var (app, http) = await StartFakesAsync(receiver.Url);
        await using var _ = app;
        await CreateSession(http, "k1", In(31));

        var until = DateTime.UtcNow.AddSeconds(10);
        while (await Stat(http, "webhooksFailed") < 1 && DateTime.UtcNow < until) await Task.Delay(100, Ct);

        Assert.Equal(1, await Stat(http, "webhooksFailed"));
        Assert.Equal(4, receiver.Count);
    }

    [Fact]
    public async Task Google_id_token_validates_against_jwks()
    {
        var (app, http) = await StartFakesAsync();
        await using var _ = app;
        var email = "Ann.Lee@Example.com";
        var token = (string?)(await Json(await http.PostAsJsonAsync("/google/id-token", new { email, name = "Ann Lee" }, Ct)))["idToken"];
        var jwks = new JsonWebKeySet(await http.GetStringAsync("/google/certs", Ct));

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidIssuer = "https://accounts.google.com",
            ValidAudience = ClientId,
        });

        Assert.True(result.IsValid, result.Exception?.Message);
        // Identity caches the JWKS and refetches only for an unknown kid, so a restarted fake must not reuse the old kid.
        Assert.Equal(jwks.Keys.Single().Kid, new JsonWebToken(token).Kid);
        Assert.NotEqual("lt-google-1", jwks.Keys.Single().Kid);
        Assert.Equal(email, result.ClaimsIdentity.FindFirst("email")!.Value);
        Assert.Equal("true", result.ClaimsIdentity.FindFirst("email_verified")!.Value, ignoreCase: true);
        Assert.Equal("lt-" + email.ToLowerInvariant(), result.ClaimsIdentity.FindFirst("sub")!.Value);
    }
}
