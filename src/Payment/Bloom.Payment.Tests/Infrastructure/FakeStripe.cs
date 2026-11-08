using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bloom.Payment.Stripe;
using Microsoft.Extensions.Time.Testing;

namespace Bloom.Payment.Tests;

/// <summary>One request the fake saw. Form pairs are in the order sent; for a GET they are the query pairs.</summary>
public sealed record StripeRequest(string Method, string Path, string? IdempotencyKey, string? StripeVersion, string? Authorization, IReadOnlyList<KeyValuePair<string, string>> Form);

/// <summary>
/// The eight Stripe endpoints of plan R11 served from memory, behind the real <c>StripeClient</c> (no test reaches the internet): sessions create / retrieve / expire,
/// refunds create / retrieve / list, payment_intents list. Anything else answers 404 and is recorded in <see cref="Unserved"/>.
/// Ids are <c>cs_test_&lt;n&gt;</c>, <c>pi_test_&lt;n&gt;</c> and <c>re_test_&lt;n&gt;</c>; paths are <c>v1/...</c> without a leading slash. Time is the test clock.
/// </summary>
public sealed class FakeStripe(FakeTimeProvider clock) : HttpMessageHandler
{
    private interface IRow
    {
        string Id { get; }
    }

    private sealed class SessionRow : IRow
    {
        public required string Id { get; init; }
        public string Status { get; set; } = "open";
        public string PaymentStatus { get; set; } = "unpaid";
        public string? PaymentIntentId { get; set; }
        public long Amount { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public string? OrderId { get; init; }
        public string? PaymentId { get; init; }
    }

    private sealed class RefundRow : IRow
    {
        public required string Id { get; init; }
        public required string Status { get; set; }
        public long Amount { get; init; }
        public string? FailureReason { get; set; }
        public string? PaymentIntentId { get; init; }
        public string? RefundId { get; init; }
        public string? OrderId { get; init; }
        public DateTimeOffset Created { get; init; }
    }

    private sealed record IntentRow(string Id, long Amount, DateTimeOffset Created) : IRow;

    private sealed class Failure(string prefix, int status, string? code, int times)
    {
        public string Prefix { get; } = prefix;
        public int Status { get; } = status;
        public string? Code { get; } = code;
        public int Left { get; set; } = times;
    }

    private readonly object _gate = new();
    private readonly List<StripeRequest> _requests = [], _unservedRequests = [];
    private readonly List<SessionRow> _sessions = [];
    private readonly List<RefundRow> _refunds = [];
    private readonly List<IntentRow> _intents = [];
    private readonly Dictionary<string, (string Fingerprint, string Id)> _keys = [];
    private readonly List<Failure> _failures = [];
    private readonly List<(string Prefix, bool StoreFirst)> _timeouts = [];
    private int _sessionCount, _intentCount, _refundCount;

    public IReadOnlyList<StripeRequest> Requests { get { lock (_gate) return [.. _requests]; } }

    /// <summary>Requests that matched none of the eight endpoints (each also answered 404). Tests may assert it is empty.</summary>
    public IReadOnlyList<string> Unserved { get { lock (_gate) return [.. _unservedRequests.Select(r => $"{r.Method} {r.Path}")]; } }

    /// <summary>Runs for every request (with its path), after it is recorded and before it is served; failures and timeouts come after.</summary>
    public Func<string, Task>? Before { get; set; }

    /// <summary>The status of the next refunds created by <c>POST v1/refunds</c>.</summary>
    public string NextRefundStatus { get; set; } = "succeeded";

    /// <summary>Items per list page, whatever <c>limit</c> says.</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>Requests whose path starts with <paramref name="pathPrefix"/>, failed ones included.</summary>
    public int Count(string pathPrefix)
    {
        pathPrefix = pathPrefix.TrimStart('/');
        lock (_gate) return _requests.Count(r => r.Path.StartsWith(pathPrefix, StringComparison.Ordinal));
    }

    /// <summary>The next <paramref name="times"/> requests under <paramref name="pathPrefix"/> answer <paramref name="status"/> with <c>error.code = code</c>, before anything is stored.</summary>
    public void Fail(string pathPrefix, int status, string? code = null, int times = 1)
    {
        lock (_gate) _failures.Add(new(pathPrefix.TrimStart('/'), status, code, times));
    }

    /// <summary>The next request under <paramref name="pathPrefix"/> never answers: it waits until the caller cancels. With <paramref name="storeFirst"/> Stripe creates the object first.</summary>
    public void TimeoutNext(string pathPrefix, bool storeFirst)
    {
        lock (_gate) _timeouts.Add((pathPrefix.TrimStart('/'), storeFirst));
    }

    /// <summary>The customer pays: the session is <c>complete</c> / <c>paid</c> and a <c>succeeded</c> PaymentIntent of the session amount exists. Returns its id.</summary>
    public string Pay(string sessionId, DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            var s = Find(_sessions, sessionId);
            if (s.Status != "open") throw new InvalidOperationException($"{sessionId} is {s.Status}, not open.");
            s.Status = "complete";
            s.PaymentStatus = "paid";
            s.PaymentIntentId = $"pi_test_{++_intentCount}";
            _intents.Add(new(s.PaymentIntentId, s.Amount, at ?? clock.GetUtcNow()));
            return s.PaymentIntentId;
        }
    }

    public void ExpireSession(string id)
    {
        lock (_gate) Find(_sessions, id).Status = "expired";
    }

    public void SetRefund(string id, string status, string? failureReason = null)
    {
        lock (_gate)
        {
            var r = Find(_refunds, id);
            r.Status = status;
            r.FailureReason = failureReason;
        }
    }

    /// <summary>A refund made in Stripe's dashboard: <c>succeeded</c>, no <c>refund_id</c> metadata.</summary>
    public string AddDashboardRefund(string piId, long amount, DateTimeOffset? at = null)
    {
        lock (_gate) return AddRefund("succeeded", amount, piId, null, null, at ?? clock.GetUtcNow()).Id;
    }

    public StripeSession Session(string id)
    {
        lock (_gate) return ToSession(Find(_sessions, id));
    }

    public StripeRefund Refund(string id)
    {
        lock (_gate) return ToRefund(Find(_refunds, id));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var form = request.Method == HttpMethod.Get ? Pairs(request.RequestUri.Query.TrimStart('?')) : Pairs(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        var recorded = new StripeRequest(request.Method.Method, path, Header(request, "Idempotency-Key"), Header(request, "Stripe-Version"), request.Headers.Authorization?.ToString(), form);
        lock (_gate) _requests.Add(recorded);
        if (Before is { } before) await before(path);

        (string Prefix, bool StoreFirst)? timeout = null;
        lock (_gate)
        {
            if (_failures.FirstOrDefault(f => f.Left > 0 && path.StartsWith(f.Prefix, StringComparison.Ordinal)) is { } failure)
            {
                failure.Left--;
                return Error(failure.Status, failure.Status >= 500 ? "api_error" : failure.Status == 429 ? "rate_limit_error" : "invalid_request_error", failure.Code);
            }
            var i = _timeouts.FindIndex(t => path.StartsWith(t.Prefix, StringComparison.Ordinal));
            if (i >= 0)
            {
                timeout = _timeouts[i];
                _timeouts.RemoveAt(i);
            }
        }

        if (timeout is null)
        {
            lock (_gate) return Serve(recorded);
        }
        if (timeout.Value.StoreFirst)
        {
            lock (_gate) Serve(recorded);
        }
        await Task.Delay(Timeout.Infinite, ct); // ends with the caller's cancellation
        throw new UnreachableException();
    }

    // callers hold _gate
    private HttpResponseMessage Serve(StripeRequest r)
    {
        var parts = r.Path.Split('/');
        var f = r.Form.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        switch (r.Method, parts)
        {
            case ("POST", ["v1", "checkout", "sessions"]): return CreateSession(r, f);
            case ("GET", ["v1", "checkout", "sessions", var id]): return Found(_sessions, id, s => Ok(SessionJson(s)));
            case ("POST", ["v1", "checkout", "sessions", var id, "expire"]):
                return Found(_sessions, id, s =>
                {
                    if (s.Status != "open") return Error(400, "invalid_request_error", "checkout_session_not_open");
                    s.Status = "expired";
                    return Ok(SessionJson(s));
                });
            case ("POST", ["v1", "refunds"]): return CreateRefund(r, f);
            case ("GET", ["v1", "refunds", var id]): return Found(_refunds, id, x => Ok(RefundJson(x)));
            case ("GET", ["v1", "refunds"]): return List(f, _refunds.Select(x => (x.Id, x.Created, (object)RefundJson(x))));
            case ("GET", ["v1", "payment_intents"]): return List(f, _intents.Select(x => (x.Id, x.Created, (object)IntentJson(x))));
        }
        _unservedRequests.Add(r);
        return Error(404, "invalid_request_error", "fake_stripe_unserved");
    }

    private HttpResponseMessage CreateSession(StripeRequest r, Dictionary<string, string> f)
    {
        if (Replay(r, out var replay)) return replay!;
        if (!f.TryGetValue("expires_at", out var e) || !long.TryParse(e, out var expires) || expires < clock.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds())
            return Error(400, "invalid_request_error", "parameter_invalid_integer");
        long amount = 0;
        for (var i = 0; f.TryGetValue($"line_items[{i}][price_data][unit_amount]", out var unit); i++)
            amount += long.Parse(unit, CultureInfo.InvariantCulture) * long.Parse(f.GetValueOrDefault($"line_items[{i}][quantity]", "1"), CultureInfo.InvariantCulture);
        var s = new SessionRow
        {
            Id = $"cs_test_{++_sessionCount}", Amount = amount, ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expires),
            OrderId = f.GetValueOrDefault("metadata[order_id]"), PaymentId = f.GetValueOrDefault("metadata[payment_id]"),
        };
        _sessions.Add(s);
        Remember(r, s.Id);
        return Ok(SessionJson(s));
    }

    private HttpResponseMessage CreateRefund(StripeRequest r, Dictionary<string, string> f)
    {
        if (Replay(r, out var replay)) return replay!;
        var refund = AddRefund(NextRefundStatus, long.Parse(f["amount"], CultureInfo.InvariantCulture), f["payment_intent"],
            f.GetValueOrDefault("metadata[refund_id]"), f.GetValueOrDefault("metadata[order_id]"), clock.GetUtcNow());
        Remember(r, refund.Id);
        return Ok(RefundJson(refund));
    }

    private RefundRow AddRefund(string status, long amount, string piId, string? refundId, string? orderId, DateTimeOffset created)
    {
        var refund = new RefundRow { Id = $"re_test_{++_refundCount}", Status = status, Amount = amount, PaymentIntentId = piId, RefundId = refundId, OrderId = orderId, Created = created };
        _refunds.Add(refund);
        return refund;
    }

    // Same key and same body: the stored object. Same key and another body: idempotency_error (spec §8).
    private bool Replay(StripeRequest r, out HttpResponseMessage? response)
    {
        response = null;
        if (r.IdempotencyKey is null || !_keys.TryGetValue(r.IdempotencyKey, out var stored)) return false;
        response = stored.Fingerprint != Fingerprint(r) ? Error(400, "idempotency_error", null)
            : stored.Id.StartsWith("cs_", StringComparison.Ordinal) ? Ok(SessionJson(Find(_sessions, stored.Id))) : Ok(RefundJson(Find(_refunds, stored.Id)));
        return true;
    }

    private void Remember(StripeRequest r, string id)
    {
        if (r.IdempotencyKey is not null) _keys[r.IdempotencyKey] = (Fingerprint(r), id);
    }

    private static string Fingerprint(StripeRequest r) => string.Join('&', r.Form.Select(p => $"{p.Key}={p.Value}"));

    // Newest first, like Stripe; the filter is created[gte] <= created < created[lt] in whole seconds.
    private HttpResponseMessage List(Dictionary<string, string> f, IEnumerable<(string Id, DateTimeOffset Created, object Json)> items)
    {
        var gte = long.Parse(f.GetValueOrDefault("created[gte]", long.MinValue.ToString()));
        var lt = long.Parse(f.GetValueOrDefault("created[lt]", long.MaxValue.ToString()));
        var ordered = items.Reverse().Where(i => i.Created.ToUnixTimeSeconds() >= gte && i.Created.ToUnixTimeSeconds() < lt).OrderByDescending(i => i.Created.ToUnixTimeSeconds()).ToList();
        var start = f.TryGetValue("starting_after", out var after) ? ordered.FindIndex(i => i.Id == after) + 1 : 0;
        var page = ordered.Skip(start).Take(PageSize).ToList();
        return Ok(new { @object = "list", data = page.Select(i => i.Json), has_more = start + page.Count < ordered.Count });
    }

    private static T Find<T>(List<T> rows, string id) where T : IRow => rows.FirstOrDefault(r => r.Id == id) ?? throw new KeyNotFoundException(id);

    private static HttpResponseMessage Found<T>(List<T> rows, string id, Func<T, HttpResponseMessage> respond) where T : IRow =>
        rows.FirstOrDefault(r => r.Id == id) is { } row ? respond(row) : Error(404, "invalid_request_error", "resource_missing");

    private object SessionJson(SessionRow s) => new
    {
        id = s.Id, @object = "checkout.session", url = $"https://checkout.stripe.test/{s.Id}", status = s.Status, payment_status = s.PaymentStatus,
        payment_intent = s.PaymentIntentId, amount_total = s.Amount, expires_at = s.ExpiresAt.ToUnixTimeSeconds(),
        metadata = new { order_id = s.OrderId, payment_id = s.PaymentId },
    };

    private static object RefundJson(RefundRow r) => new
    {
        id = r.Id, @object = "refund", status = r.Status, amount = r.Amount, failure_reason = r.FailureReason, payment_intent = r.PaymentIntentId,
        created = r.Created.ToUnixTimeSeconds(),
        metadata = new Dictionary<string, string>(new[] { ("refund_id", r.RefundId), ("order_id", r.OrderId) }.Where(p => p.Item2 is not null).Select(p => KeyValuePair.Create(p.Item1, p.Item2!))),
    };

    private static object IntentJson(IntentRow p) => new { id = p.Id, @object = "payment_intent", status = "succeeded", amount = p.Amount, created = p.Created.ToUnixTimeSeconds() };

    private static StripeSession ToSession(SessionRow s) =>
        new(s.Id, $"https://checkout.stripe.test/{s.Id}", s.Status, s.PaymentStatus, s.PaymentIntentId, s.Amount, s.ExpiresAt);

    private static StripeRefund ToRefund(RefundRow r) =>
        new(r.Id, r.Status, r.Amount, r.FailureReason, Guid.TryParse(r.RefundId, out var id) ? id : null, r.Created);

    private static string? Header(HttpRequestMessage r, string name) => r.Headers.TryGetValues(name, out var v) ? v.Single() : null;

    private static List<KeyValuePair<string, string>> Pairs(string encoded) =>
        [.. encoded.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p =>
        {
            var kv = p.Split('=', 2);
            return KeyValuePair.Create(Decode(kv[0]), kv.Length > 1 ? Decode(kv[1]) : "");
        })];

    private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    private static HttpResponseMessage Ok(object body) => Json(HttpStatusCode.OK, body);

    private static HttpResponseMessage Error(int status, string type, string? code) =>
        Json((HttpStatusCode)status, new { error = code is null ? new Dictionary<string, string> { ["type"] = type } : new Dictionary<string, string> { ["type"] = type, ["code"] = code } });

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
